//
//      Copyright (C) 2012-2014 DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cassandra.Collections;
using Cassandra.Serialization;
using Cassandra.Tasks;

namespace Cassandra
{
    /// <summary>
    /// Represents a pool of connections to a host
    /// </summary>
    internal class HostConnectionPool : IDisposable
    {
        private static readonly Logger Logger = new Logger(typeof(HostConnectionPool));
        private const int ConnectionIndexOverflow = int.MaxValue - 1000000;
        private const long BetweenResizeDelay = 2000;

        /// <summary>
        /// Represents the possible states of the pool.
        /// Possible state transitions:
        ///  - From Init to Closing: The pool must be closed because the host is ignored or because the pool should
        ///    not attempt more reconnections (another pool is trying to reconnect to a UP host).
        ///  - From Init to ShuttingDown: The pool is being shutdown as a result of a client shutdown.
        ///  - From Closing to Init: The pool finished closing connections (is now ignored) and it resets to
        ///    initial state in case the host is marked as local/remote in the future.
        ///    // ^ TODO, how to control it? Host.Up event?
        ///  - From Closing to ShuttingDown (rare): It was marked as ignored, now the client is being shutdown.
        ///  - From ShuttingDown to Shutdown: Finished shutting down, the pool should not be reused.
        /// </summary>
        private static class PoolState
        {
            /// <summary>
            /// Initial state: open / opening / ready to be opened
            /// </summary>
            public const int Init = 0;
            /// <summary>
            /// When the pool is being closed as part of a distance change
            /// </summary>
            public const int Closing = 1;
            /// <summary>
            /// When the pool is being shutdown for good
            /// </summary>
            public const int ShuttingDown = 2;
            /// <summary>
            /// When the pool has being shutdown
            /// </summary>
            public const int Shutdown = 3;
        }

        private readonly Host _host;
        private readonly Configuration _config;
        private readonly Serializer _serializer;
        private readonly CopyOnWriteList<Connection> _connections = new CopyOnWriteList<Connection>();
        private readonly HashedWheelTimer _timer;
        private volatile IReconnectionSchedule _reconnectionSchedule;
        private volatile int _expectedConnectionLength;
        private volatile int _maxInflightThreshold;
        private volatile int _maxConnectionLength;
        private volatile HashedWheelTimer.ITimeout _resizingEndTimeout;
        private int _poolResizing;
        private int _state = PoolState.Init;
        private HashedWheelTimer.ITimeout _newConnectionTimeout;
        private TaskCompletionSource<Connection> _connectionOpenTcs;
        private int _connectionIndex;

        public event Action<Host, HostConnectionPool> AllConnectionClosed;

        public bool HasConnections
        {
            get { return _connections.Count > 0; }
        }

        public int OpenConnections
        {
            get { return _connections.Count; }
        }

        public bool IsClosing
        {
            get { return Volatile.Read(ref _state) != PoolState.Init; }
        }

        public HostConnectionPool(Host host, Configuration config, Serializer serializer)
        {
            _host = host;
            _host.Down += OnHostDown;
            _host.Up += OnHostUp;
            _host.Remove += OnHostRemoved;
            _config = config;
            _serializer = serializer;
            _timer = config.Timer;
            _reconnectionSchedule = config.Policies.ReconnectionPolicy.NewSchedule();
            _expectedConnectionLength = 1;
        }

        /// <summary>
        /// Gets an open connection from the host pool (creating if necessary).
        /// It returns null if the load balancing policy didn't allow connections to this host.
        /// </summary>
        public async Task<Connection> BorrowConnection()
        {
            var connections = await EnsureCreate().ConfigureAwait(false);
            if (connections.Length == 0)
            {
                return null;
            }
            var c = MinInFlight(connections, ref _connectionIndex, _maxInflightThreshold);
            ConsiderResizingPool(c.InFlight);
            return c;
        }

        private void CancelNewConnectionTimeout(HashedWheelTimer.ITimeout newTimeout = null)
        {
            var previousTimeout = Interlocked.Exchange(ref _newConnectionTimeout, newTimeout);
            if (previousTimeout != null)
            {
                // Clear previous reconnection attempt timeout
                previousTimeout.Cancel();
            }
        }

        public void CheckHealth(Connection c)
        {
            var timedOutOps = c.TimedOutOperations;
            if (timedOutOps < _config.SocketOptions.DefunctReadTimeoutThreshold)
            {
                return;
            }
            Logger.Warning("Connection to {0} considered as unhealthy after {1} timed out operations", 
                _host.Address, timedOutOps);
            //Defunct: close it and remove it from the pool
            OnConnectionClosing(c);
            c.Dispose();
        }

        public void ConsiderResizingPool(int inFlight)
        {
            if (inFlight < _maxInflightThreshold)
            {
                // The requests in-flight are normal
                return;
            }
            if (_expectedConnectionLength >= _maxConnectionLength)
            {
                // We can not add more connections
                return;
            }
            if (_connections.Count < _expectedConnectionLength)
            {
                // The pool is still trying to acquire the correct size
                return;
            }
            var canResize = Interlocked.Exchange(ref _poolResizing, 1) == 0;
            if (!canResize)
            {
                // There is already another thread resizing the pool
                return;
            }
            _expectedConnectionLength++;
            Logger.Info("Increasing pool #{0} size to {1}, as in-flight requests are above threshold ({2})", 
                GetHashCode(), _expectedConnectionLength, _maxInflightThreshold);
            StartCreatingConnection(null);
            _resizingEndTimeout = _timer.NewTimeout(_ => Interlocked.Exchange(ref _poolResizing, 0), null, BetweenResizeDelay);
        }

        /// <summary>
        /// Releases the resources associated with the pool.
        /// </summary>
        public void Dispose()
        {
            var markShuttingDown = 
                (Interlocked.CompareExchange(ref _state, PoolState.ShuttingDown, PoolState.Init) == PoolState.Init) ||
                (Interlocked.CompareExchange(ref _state, PoolState.ShuttingDown, PoolState.Closing) == PoolState.Init);
            if (!markShuttingDown)
            {
                // The pool is already being shutdown, never mind
                return;
            }
            var connections = _connections.ClearAndGet();
            foreach (var c in connections)
            {
                c.Dispose();
            }
            _host.Up -= OnHostUp;
            _host.Down -= OnHostDown;
            _host.Remove -= OnHostRemoved;
            var t = _resizingEndTimeout;
            if (t != null)
            {
                t.Cancel();
            }
            CancelNewConnectionTimeout();
            Interlocked.Exchange(ref _state, PoolState.Shutdown);
        }

        public virtual async Task<Connection> DoCreateAndOpen()
        {
            var c = new Connection(_serializer, _host.Address, _config);
            try
            {
                await c.Open().ConfigureAwait(false);
            }
            catch
            {
                c.Dispose();
                throw;
            }
            if (_config.GetPoolingOptions(_serializer.ProtocolVersion).GetHeartBeatInterval() > 0)
            {
                c.OnIdleRequestException += ex => OnIdleRequestException(c, ex);
            }
            c.Closing += OnConnectionClosing;
            return c;
        }

        /// <summary>
        /// Gets the connection with the minimum number of InFlight requests.
        /// Only checks for index + 1 and index, to avoid a loop of all connections.
        /// </summary>
        /// <param name="connections">A snapshot of the pool of connections</param>
        /// <param name="connectionIndex">Current round-robin index</param>
        /// <param name="inFlightThreshold">
        /// The max amount of in-flight requests that cause this method to continue
        /// iterating until finding the connection with min number of in-flight requests.
        /// </param>
        public static Connection MinInFlight(Connection[] connections, ref int connectionIndex, int inFlightThreshold)
        {
            if (connections.Length == 1)
            {
                return connections[0];
            }
            //It is very likely that the amount of InFlight requests per connection is the same
            //Do round robin between connections, skipping connections that have more in flight requests
            var index = Interlocked.Increment(ref connectionIndex);
            if (index > ConnectionIndexOverflow)
            {
                //Overflow protection, not exactly thread-safe but we can live with it
                Interlocked.Exchange(ref connectionIndex, 0);
            }
            Connection c = null;
            for (var i = index; i < index + connections.Length; i++)
            {
                c = connections[i % connections.Length];
                var previousConnection = connections[(i - 1) % connections.Length];
                // Avoid multiple volatile reads
                var inFlight = c.InFlight;
                var previousInFlight = previousConnection.InFlight;
                if (previousInFlight < inFlight)
                {
                    c = previousConnection;
                    inFlight = previousInFlight;
                }
                if (inFlight < inFlightThreshold)
                {
                    // We should avoid traversing all the connections
                    // We have a connection with a decent amount of in-flight requests
                    break;
                }
            }
            return c;
        }

        private void OnConnectionClosing(Connection c = null)
        {
            int currentLength;
            if (c != null)
            {
                var removalInfo = _connections.RemoveAndCount(c);
                currentLength = removalInfo.Item2;
                var hasBeenRemoved = removalInfo.Item1;
                if (!hasBeenRemoved)
                {
                    // It has been already removed (via event or direct call)
                    // When it was removed, all the following checks have been made
                    // No point in doing them again
                    return;
                }
                Logger.Info("Pool #{0} for host {1} removed a connection, new length: {2}",
                    GetHashCode(), _host.Address, currentLength);
            }
            else
            {
                currentLength = _connections.Count;
            }
            if (IsClosing || currentLength >= _expectedConnectionLength)
            {
                // No need to reconnect
                return;
            }
            if (currentLength == 0)
            {
                // All connections have been closed
                // If the node is UP, we should stop attempting to reconnect
                if (_host.IsUp && AllConnectionClosed != null)
                {
                    // Raise the event and wait for a caller to decide
                    AllConnectionClosed(_host, this);
                    return;
                }
            }
            SetNewConnectionTimeout(_reconnectionSchedule);
        }

        private void OnHostRemoved()
        {
            //TODO: Drain and shutdown
            throw new NotImplementedException();
        }

        public void OnHostUp(Host h)
        {
            if (_connections.Count > 0)
            {
                // This was the pool that was reconnecting, the pool is already getting the appropriate size
                return;
            }
            Logger.Info("Pool #{0} for host {1} attempting to reconnect as host is UP", GetHashCode(), _host.Address);
            ScheduleReconnection(true);
        }

        private void OnHostDown(Host h)
        {
            // Cancel the outstanding timeout (if any)
            // If the timeout already elapsed, a connection could be been created anyway
            CancelNewConnectionTimeout();
        }

        /// <summary>
        /// Handler that gets invoked when if there is a socket exception when making a heartbeat/idle request
        /// </summary>
        private void OnIdleRequestException(Connection c, Exception ex)
        {
            Logger.Warning("Connection to {0} considered as unhealthy after idle timeout exception: {1}",
                _host.Address, ex);
            OnConnectionClosing(c);
            c.Dispose();
        }

        /// <summary>
        /// Sets the state of the pool to Closing
        /// </summary>
        public void StartClosing()
        {
            //TODO
            var isClosing = Interlocked.CompareExchange(ref _state, PoolState.Closing, PoolState.Init) == PoolState.Init;
            if (!isClosing)
            {
                // It was in another state, don't mind
                return;
            }
            CancelNewConnectionTimeout();
        }

        /// <summary>
        /// Adds a new reconnection timeout using a new schedule.
        /// Resets the status of the pool to allow further reconnections.
        /// </summary>
        public void ScheduleReconnection(bool immediate = false)
        {
            var schedule = _config.Policies.ReconnectionPolicy.NewSchedule();
            _reconnectionSchedule = schedule;
            SetNewConnectionTimeout(immediate ? null : schedule);
        }

        private void SetNewConnectionTimeout(IReconnectionSchedule schedule)
        {
            if (schedule != null && _reconnectionSchedule != schedule)
            {
                // There's another reconnection schedule, leave it
                return;
            }
            HashedWheelTimer.ITimeout timeout = null;
            if (schedule != null)
            {
                // Schedule the creation
                var delay = schedule.NextDelayMs();
                Logger.Info("Scheduling reconnection to {0} in {1}ms", _host.Address, delay);
                timeout = _timer.NewTimeout(_ => StartCreatingConnection(schedule), null, delay);
            }
            CancelNewConnectionTimeout(timeout);
            if (schedule == null)
            {
                // Start creating immediately after de-scheduling the timer
                StartCreatingConnection(null);
            }
        }

        /// <summary>
        /// Asynchronously starts to create a new connection (if its not already being created).
        /// A <c>null</c> schedule signals that the pool is not reconnecting but growing to the expected size.
        /// </summary>
        /// <param name="schedule"></param>
        private void StartCreatingConnection(IReconnectionSchedule schedule)
        {
            var count = _connections.Count;
            if (count >= _expectedConnectionLength)
            {
                return;
            }
            if (schedule != null && schedule != _reconnectionSchedule)
            {
                // There's another reconnection schedule, leave it
                return;
            }
            CreateOpenConnection().ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    StartCreatingConnection(null);
                    _host.BringUpIfDown();
                    return;
                }
                // The connection could not be opened
                if (IsClosing)
                {
                    // don't mind, the pool is not supposed to be open
                    return;
                }
                if (schedule == null)
                {
                    // As it failed, we need a new schedule for the following attempts
                    schedule = _config.Policies.ReconnectionPolicy.NewSchedule();
                    _reconnectionSchedule = schedule;
                }
                if (schedule != _reconnectionSchedule)
                {
                    // There's another reconnection schedule, leave it
                    return;
                }
                OnConnectionClosing();
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>
        /// Opens one connection. 
        /// If a connection is being opened it yields the same task, preventing creation in parallel.
        /// </summary>
        /// <exception cref="SocketException">Throws a SocketException when the connection could not be established with the host</exception>
        /// <exception cref="AuthenticationException" />
        /// <exception cref="UnsupportedProtocolVersionException"></exception>
        private async Task<Connection> CreateOpenConnection()
        {
            var concurrentOpenTcs = Volatile.Read(ref _connectionOpenTcs);
            // Try to exit early (cheap) as there could be another thread creating / finishing creating
            if (concurrentOpenTcs != null)
            {
                // There is another thread opening a new connection
                return await concurrentOpenTcs.Task.ConfigureAwait(false);
            }
            var tcs = new TaskCompletionSource<Connection>();
            // Try to set the creation task source
            concurrentOpenTcs = Interlocked.CompareExchange(ref _connectionOpenTcs, tcs, null);
            if (concurrentOpenTcs != null)
            {
                // There is another thread opening a new connection
                return await concurrentOpenTcs.Task.ConfigureAwait(false);
            }
            if (IsClosing)
            {
                return await FinishOpen(tcs, GetNotConnectedException()).ConfigureAwait(false);
            }
            // Before creating, make sure that its still needed
            // This method is the only one that adds new connections
            // But we don't control the removal, use snapshot
            var connectionsSnapshot = _connections.GetSnapshot();
            if (connectionsSnapshot.Length >= _expectedConnectionLength)
            {
                if (connectionsSnapshot.Length == 0)
                {
                    // Avoid race condition while removing
                    return await FinishOpen(tcs, GetNotConnectedException()).ConfigureAwait(false);
                }
                return await FinishOpen(tcs, null, connectionsSnapshot[0]).ConfigureAwait(false);
            }
            Logger.Info("Creating a new connection to {0}", _host.Address);
            Connection c;
            try
            {
                c = await DoCreateAndOpen().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Info("Connection to {0} could not be created: {1}", _host.Address, ex);
                // Can not await on catch on C# 5..
                FinishOpen(tcs, ex).ConfigureAwait(false);
                throw;
            }
            if (IsClosing)
            {
                Logger.Info("Connection to {0} opened successfully but pool #{1} was being closed", _host.Address);
                c.Dispose();
                return await FinishOpen(tcs, GetNotConnectedException()).ConfigureAwait(false);
            }
            var newLength = _connections.AddNew(c);
            Logger.Info("Connection to {0} opened successfully, pool #{1} length: {2}", 
                _host.Address, GetHashCode(), newLength);
            if (IsClosing)
            {
                // We haven't use a CAS operation, so it's possible that the pool is being closed while adding a new
                // connection, we should remove it.
                Logger.Info("Connection to {0} opened successfully and added to the pool #{1} but it was being closed",
                    _host.Address);
                _connections.Remove(c);
                c.Dispose();
                return await FinishOpen(tcs, GetNotConnectedException()).ConfigureAwait(false);
            }
            return await FinishOpen(tcs, null, c).ConfigureAwait(false);
        }

        private Task<Connection> FinishOpen(TaskCompletionSource<Connection> tcs, Exception ex, Connection c = null)
        {
            Interlocked.Exchange(ref _connectionOpenTcs, null);
            if (ex != null)
            {
                tcs.TrySetException(ex);
            }
            else
            {
                tcs.TrySetResult(c);
            }
            return tcs.Task;
        }

        private static SocketException GetNotConnectedException()
        {
            return new SocketException((int)SocketError.NotConnected);
        }

        /// <summary>
        /// Ensures that the pool has at least contains 1 connection to the host.
        /// </summary>
        public async Task<Connection[]> EnsureCreate()
        {
            var connections = _connections.GetSnapshot();
            if (connections.Length > 0)
            {
                // Use snapshot to return as early as possible
                return connections;
            }
            try
            {
                var c = await CreateOpenConnection().ConfigureAwait(false);
                StartCreatingConnection(null);
                return new[] { c };
            }
            catch (Exception)
            {
                OnConnectionClosing();
                throw;
            }
        }

        public void SetDistance(HostDistance distance)
        {
            var poolingOptions = _config.GetPoolingOptions(_serializer.ProtocolVersion);
            _expectedConnectionLength = poolingOptions.GetCoreConnectionsPerHost(distance);
            _maxInflightThreshold =  poolingOptions.GetMaxSimultaneousRequestsPerConnectionTreshold(distance);
            _maxConnectionLength = poolingOptions.GetMaxConnectionPerHost(distance);
        }
    }
}
