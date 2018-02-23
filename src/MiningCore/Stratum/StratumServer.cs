﻿/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using MiningCore.Banning;
using MiningCore.Buffers;
using MiningCore.Configuration;
using MiningCore.JsonRpc;
using MiningCore.Time;
using MiningCore.Util;
using NetMQ;
using Newtonsoft.Json;
using NLog;
using Contract = MiningCore.Contracts.Contract;

namespace MiningCore.Stratum
{
    public abstract class StratumServer
    {
        protected StratumServer(IComponentContext ctx, IMasterClock clock)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(clock, nameof(clock));

            this.ctx = ctx;
            this.clock = clock;
        }

        protected readonly Dictionary<string, StratumClient> clients = new Dictionary<string, StratumClient>();

        protected readonly IComponentContext ctx;
        protected readonly IMasterClock clock;
        protected readonly Dictionary<int, Socket> ports = new Dictionary<int, Socket>();
        protected ClusterConfig clusterConfig;
        protected IBanManager banManager;
        protected bool disableConnectionLogging = false;
        protected ILogger logger;

        protected abstract string LogCat { get; }

        public void StartListeners(string id, params IPEndPoint[] stratumPorts)
        {
            Contract.RequiresNonNull(stratumPorts, nameof(stratumPorts));

            // every port gets serviced by a dedicated loop thread
            foreach(var endpoint in stratumPorts)
            {
                var thread = new Thread(async _ =>
                {
                    var server = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    server.Bind(endpoint);
                    server.Listen(512);

                    lock (ports)
                    {
                        ports[endpoint.Port] = server;
                    }

                    logger.Info(() => $"[{LogCat}] Stratum port {endpoint.Address}:{endpoint.Port} online");

                    while (true)
                    {
                        try
                        {
                            var socket = await server.AcceptAsync();

                            // prepare socket
                            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, 1000);
                            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.NoDelay, true);

                            // hand over
                            #pragma warning disable 4014
                            OnClientConnected(new NetworkStream(socket, true), (IPEndPoint)socket.RemoteEndPoint, endpoint);
                            #pragma warning restore 4014
                        }

                        catch (Exception ex)
                        {
                            logger.Error(ex, () => Thread.CurrentThread.Name);
                        }
                    }
                }) { Name = $"UvLoopThread {id}:{endpoint.Port}" };

                thread.Start();
            }
        }

        public void StopListeners()
        {
            lock(ports)
            {
                var portValues = ports.Values.ToArray();

                for(int i = 0; i < portValues.Length; i++)
                {
                    var socket = portValues[i];

                    socket.Close();
                }
            }
        }

        private void OnClientConnected(NetworkStream stream, IPEndPoint remoteEndpoint, IPEndPoint localEndpoint)
        {
            try
            {
                // get rid of banned clients as early as possible
                if (banManager?.IsBanned(remoteEndpoint.Address) == true)
                {
                    logger.Debug(() => $"[{LogCat}] Disconnecting banned ip {remoteEndpoint.Address}");
                    stream.Close();
                    return;
                }

                var connectionId = CorrelationIdGenerator.GetNextId();
                logger.Debug(() => $"[{LogCat}] Accepting connection [{connectionId}] from {remoteEndpoint.Address}:{remoteEndpoint.Port}");

                // setup client
                var client = new StratumClient(localEndpoint, connectionId);

                // register client
                lock (clients)
                {
                    clients[connectionId] = client;
                }

                OnConnect(client);

                client.Init(stream, remoteEndpoint, clock,
                    data => OnReceiveAsync(client, data),
                    () => OnReceiveComplete(client),
                    ex => OnReceiveError(client, ex));
            }

            catch(Exception ex)
            {
                logger.Error(ex, () => nameof(OnClientConnected));
            }
        }

        protected virtual async void OnReceiveAsync(StratumClient client, PooledArraySegment<byte> data)
        {
            using (data)
            {
                JsonRpcRequest request = null;

                try
                {
                    // boot pre-connected clients
                    if (banManager?.IsBanned(client.RemoteEndpoint.Address) == true)
                    {
                        logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Disconnecting banned client @ {client.RemoteEndpoint.Address}");
                        DisconnectClient(client);
                        return;
                    }

                    // de-serialize
                    logger.Trace(() => $"[{LogCat}] [{client.ConnectionId}] Received request data: {StratumConstants.Encoding.GetString(data.Array, 0, data.Size)}");
                    request = client.DeserializeRequest(data);

                    // dispatch
                    if (request != null)
                    {
                        logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Dispatching request '{request.Method}' [{request.Id}]");
                        await OnRequestAsync(client, new Timestamped<JsonRpcRequest>(request, clock.Now));
                    }

                    else
                        logger.Trace(() => $"[{LogCat}] [{client.ConnectionId}] Unable to deserialize request");
                }

                catch (JsonReaderException jsonEx)
                {
                    // junk received (no valid json)
                    logger.Error(() => $"[{LogCat}] [{client.ConnectionId}] Connection json error state: {jsonEx.Message}");

                    if (clusterConfig.Banning?.BanOnJunkReceive.HasValue == false || clusterConfig.Banning?.BanOnJunkReceive == true)
                    {
                        logger.Info(() => $"[{LogCat}] [{client.ConnectionId}] Banning client for sending junk");
                        banManager?.Ban(client.RemoteEndpoint.Address, TimeSpan.FromMinutes(30));
                    }
                }

                catch (Exception ex)
                {
                    if (request != null)
                        logger.Error(ex, () => $"[{LogCat}] [{client.ConnectionId}] Error processing request {request.Method} [{request.Id}]");
                }
            }
        }

        protected virtual void OnReceiveError(StratumClient client, Exception ex)
        {
            switch (ex)
            {
                case SocketException opEx:
                    // log everything but ECONNRESET which just indicates the client disconnecting
                    if (opEx.SocketErrorCode != SocketError.ConnectionReset)
                        logger.Error(() => $"[{LogCat}] [{client.ConnectionId}] Connection error state: {ex.Message}");
                    break;

                default:
                    logger.Error(() => $"[{LogCat}] [{client.ConnectionId}] Connection error state: {ex.Message}");
                    break;
            }

            DisconnectClient(client);
        }

        protected virtual void OnReceiveComplete(StratumClient client)
        {
            logger.Debug(() => $"[{LogCat}] [{client.ConnectionId}] Received EOF");

            DisconnectClient(client);
        }

        protected virtual void DisconnectClient(StratumClient client)
        {
            Contract.RequiresNonNull(client, nameof(client));

            var subscriptionId = client.ConnectionId;

            client.Disconnect();

            if (!string.IsNullOrEmpty(subscriptionId))
            {
                // unregister client
                lock(clients)
                {
                    clients.Remove(subscriptionId);
                }
            }

            OnDisconnect(subscriptionId);
        }

        protected void ForEachClient(Action<StratumClient> action)
        {
            StratumClient[] tmp;

            lock(clients)
            {
                tmp = clients.Values.ToArray();
            }

            foreach(var client in tmp)
            {
                try
                {
                    action(client);
                }

                catch(Exception ex)
                {
                    logger.Error(ex);
                }
            }
        }

        protected abstract void OnConnect(StratumClient client);

        protected virtual void OnDisconnect(string subscriptionId)
        {
        }

        protected abstract Task OnRequestAsync(StratumClient client,
            Timestamped<JsonRpcRequest> request);
    }
}
