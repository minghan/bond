// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Bond.Comm.Epoxy
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Bond.Comm.Layers;
    using Bond.Comm.Service;
    using Bond.IO.Safe;
    using Bond.Protocols;

    public class EpoxyConnection : Connection, IRequestResponseConnection, IEventConnection
    {
        static readonly EpoxyConfig EmptyConfig = new EpoxyConfig();

        private enum ConnectionType
        {
            Client,
            Server
        }

        [Flags]
        private enum State
        {
            None = 0,
            Created = 0x01,
            ClientSendConfig = 0x02,
            ClientExpectConfig = 0x04,
            ServerExpectConfig = 0x08,
            ServerSendConfig = 0x10,
            Connected = 0x20,
            SendProtocolError = 0x40,
            Disconnecting = 0x80,
            Disconnected = 0x100,
            All = Created | ClientSendConfig | ClientExpectConfig | ServerExpectConfig | ServerSendConfig | Connected | SendProtocolError | Disconnecting | Disconnected,
        }

        readonly ConnectionType connectionType;

        readonly EpoxyTransport parentTransport;
        readonly EpoxyListener parentListener;
        readonly ServiceHost serviceHost;

        readonly EpoxySocket netSocket;

        readonly ResponseMap responseMap;

        State state;
        readonly TaskCompletionSource<bool> startTask;
        readonly TaskCompletionSource<bool> stopTask;
        readonly CancellationTokenSource shutdownTokenSource;

        long prevConversationId;

        ProtocolErrorCode protocolError;
        Error errorDetails;

        // this member is used to capture any handshake errors
        ProtocolError handshakeError;

        readonly private ConnectionMetrics connectionMetrics = new ConnectionMetrics();
        private Stopwatch duration;

        private EpoxyConnection(
            ConnectionType connectionType,
            EpoxyTransport parentTransport,
            EpoxyListener parentListener,
            ServiceHost serviceHost,
            Socket socket)
        {
            Debug.Assert(parentTransport != null);
            Debug.Assert(connectionType != ConnectionType.Server || parentListener != null, "Server connections must have a listener");
            Debug.Assert(serviceHost != null);
            Debug.Assert(socket != null);

            this.connectionType = connectionType;

            this.parentTransport = parentTransport;
            this.parentListener = parentListener;
            this.serviceHost = serviceHost;

            netSocket = new EpoxySocket(socket);

            // cache these so we can use them after the socket has been shutdown
            LocalEndPoint = (IPEndPoint) socket.LocalEndPoint;
            RemoteEndPoint = (IPEndPoint) socket.RemoteEndPoint;

            responseMap = new ResponseMap();

            state = State.Created;
            startTask = new TaskCompletionSource<bool>();
            stopTask = new TaskCompletionSource<bool>();
            shutdownTokenSource = new CancellationTokenSource();

            // start at -1 or 0 so the first conversation ID is 1 or 2.
            prevConversationId = (connectionType == ConnectionType.Client) ? -1 : 0;

            connectionMetrics.connection_id = Guid.NewGuid().ToString();
            connectionMetrics.local_endpoint = LocalEndPoint.ToString();
            connectionMetrics.remote_endpoint = RemoteEndPoint.ToString();
        }

        internal static EpoxyConnection MakeClientConnection(
            EpoxyTransport parentTransport,
            Socket clientSocket)
        {
            const EpoxyListener parentListener = null;

            return new EpoxyConnection(
                ConnectionType.Client,
                parentTransport,
                parentListener,
                new ServiceHost(parentTransport),
                clientSocket);
        }

        internal static EpoxyConnection MakeServerConnection(
            EpoxyTransport parentTransport,
            EpoxyListener parentListener,
            ServiceHost serviceHost,
            Socket socket)
        {
            return new EpoxyConnection(
                ConnectionType.Server,
                parentTransport,
                parentListener,
                serviceHost,
                socket);
        }

        /// <summary>
        /// Get this connection's local endpoint.
        /// </summary>
        public IPEndPoint LocalEndPoint { get; private set; }

        /// <summary>
        /// Get this connection's remote endpoint.
        /// </summary>
        public IPEndPoint RemoteEndPoint { get; private set; }

        public override string ToString()
        {
            return $"{nameof(EpoxyConnection)}(local: {LocalEndPoint}, remote: {RemoteEndPoint})";
        }

        internal static Frame MessageToFrame(ulong conversationId, string methodName, PayloadType type, IMessage payload, IBonded layerData)
        {
            var frame = new Frame();

            {
                var headers = new EpoxyHeaders
                {
                    conversation_id = conversationId,
                    payload_type = type,
                    method_name = methodName ?? string.Empty, // method_name is not nullable
                };

                if (payload.IsError)
                {
                    headers.error_code = payload.Error.Deserialize<Error>().error_code;
                }
                else
                {
                    headers.error_code = (int)ErrorCode.OK;
                }

                var outputBuffer = new OutputBuffer(150);
                var fastWriter = new FastBinaryWriter<OutputBuffer>(outputBuffer);
                Serialize.To(fastWriter, headers);

                frame.Add(new Framelet(FrameletType.EpoxyHeaders, outputBuffer.Data));
            }

            if (layerData != null)
            {
                var outputBuffer = new OutputBuffer(150);
                var compactWriter = new CompactBinaryWriter<OutputBuffer>(outputBuffer);
                // TODO: See TODO below about issues with IBonded Marshal.TO(...)
                compactWriter.WriteVersion();
                layerData.Serialize(compactWriter);
                frame.Add(new Framelet(FrameletType.LayerData, outputBuffer.Data));
            }

            {
                var userData = payload.IsError ? (IBonded)payload.Error : (IBonded)payload.RawPayload;

                var outputBuffer = new OutputBuffer(1024);
                var compactWriter = new CompactBinaryWriter<OutputBuffer>(outputBuffer);
                // TODO: marshal dies on IBonded Marshal.To(compactWriter, request)
                // understand more deeply why and consider fixing
                compactWriter.WriteVersion();
                userData.Serialize(compactWriter);

                frame.Add(new Framelet(FrameletType.PayloadData, outputBuffer.Data));
            }

            return frame;
        }

        internal static Frame MakeConfigFrame()
        {
            var outputBuffer = new OutputBuffer(1);
            var fastWriter = new FastBinaryWriter<OutputBuffer>(outputBuffer);
            Serialize.To(fastWriter, EmptyConfig);

            var frame = new Frame(1);
            frame.Add(new Framelet(FrameletType.EpoxyConfig, outputBuffer.Data));
            return frame;
        }

        internal static Frame MakeProtocolErrorFrame(ProtocolErrorCode errorCode, Error details)
        {
            var protocolError = new ProtocolError
            {
                error_code = errorCode,
                details = details == null ? null : new Bonded<Error>(details)
            };

            var outputBuffer = new OutputBuffer(16);
            var fastWriter = new FastBinaryWriter<OutputBuffer>(outputBuffer);
            Serialize.To(fastWriter, protocolError);

            var frame = new Frame(1);
            frame.Add(new Framelet(FrameletType.ProtocolError, outputBuffer.Data));
            return frame;
        }

        private async Task<IMessage> SendRequestAsync<TPayload>(string methodName, IMessage<TPayload> request)
        {
            var conversationId = AllocateNextConversationId();

            var sendContext = new EpoxySendContext(this);
            IBonded layerData;
            Error layerError = LayerStackUtils.ProcessOnSend(parentTransport.LayerStack, MessageType.Request, sendContext, out layerData);

            if (layerError != null)
            {
                Log.Error("{0}.{1}: Sending request {2}/{3} failed due to layer error (Code: {4}, Message: {5}).",
                            this, nameof(SendRequestAsync), conversationId, methodName, layerError.error_code,
                            layerError.message);
                return Message.FromError(layerError);
            }

            var frame = MessageToFrame(conversationId, methodName, PayloadType.Request, request, layerData);

            Log.Debug("{0}.{1}: Sending request {2}/{3}.", this, nameof(SendRequestAsync), conversationId, methodName);
            var responseTask = responseMap.Add(conversationId);

            bool wasSent = await SendFrameAsync(frame);
            Log.Debug(
                "{0}.{1}: Sending request {2}/{3} {4}.",
                this, nameof(SendRequestAsync), conversationId, methodName, wasSent ? "succeded" : "failed");

            if (!wasSent)
            {
                bool wasCompleted = responseMap.Complete(
                    conversationId,
                    Message.FromError(new Error
                    {
                        error_code = (int) ErrorCode.TransportError,
                        message = "Request could not be sent"
                    }));

                if (!wasCompleted)
                {
                    Log.Information(
                        "{0}.{1} Unsuccessfully sent request {2}/{3} still received response.",
                        this, nameof(SendRequestAsync), conversationId, methodName);
                }
            }

            return await responseTask;
        }

        private async Task SendReplyAsync(ulong conversationId, IMessage response)
        {
            var sendContext = new EpoxySendContext(this);
            IBonded layerData;
            Error layerError = LayerStackUtils.ProcessOnSend(parentTransport.LayerStack, MessageType.Response, sendContext, out layerData);

            // If there was a layer error, replace the response with the layer error
            if (layerError != null)
            {
                Log.Error("{0}.{1}: Sending reply for conversation ID {2} failed due to layer error (Code: {3}, Message: {4}).",
                            this, nameof(SendReplyAsync), conversationId, layerError.error_code, layerError.message);
                response = Message.FromError(layerError);
            }

            var frame = MessageToFrame(conversationId, null, PayloadType.Response, response, layerData);
            Log.Debug("{0}.{1}: Sending reply for conversation ID {2}.", this, nameof(SendReplyAsync), conversationId);

            bool wasSent = await SendFrameAsync(frame);
            Log.Debug(
                "{0}.{1}: Sending reply for conversation ID {2} {3}.",
                this, nameof(SendReplyAsync), conversationId, wasSent ? "succeded" : "failed");
        }

        private async Task<bool> SendFrameAsync(Frame frame)
        {
            try
            {
                Stream networkStream = netSocket.NetworkStream;

                await netSocket.WriteLock.WaitAsync();
                try
                {
                    await frame.WriteAsync(networkStream);
                }
                finally
                {
                    netSocket.WriteLock.Release();
                }

                await networkStream.FlushAsync();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                Log.Error(ex, "{0}.{1}: While writing a Frame to the network: {2}", this, nameof(SendFrameAsync),
                    ex.Message);
                return false;
            }
        }

        internal async Task SendEventAsync(string methodName, IMessage message)
        {
            var conversationId = AllocateNextConversationId();

            var sendContext = new EpoxySendContext(this);
            IBonded layerData;
            Error layerError = LayerStackUtils.ProcessOnSend(parentTransport.LayerStack, MessageType.Event, sendContext, out layerData);

            if (layerError != null)
            {
                Log.Error("{0}.{1}: Sending event {2}/{3} failed due to layer error (Code: {4}, Message: {5}).",
                            this, nameof(SendEventAsync), conversationId, methodName, layerError.error_code, layerError.message);
                return;
            }

            var frame = MessageToFrame(conversationId, methodName, PayloadType.Event, message, layerData);

            Log.Debug("{0}.{1}: Sending event {2}/{3}.", this, nameof(SendEventAsync), conversationId, methodName);

            bool wasSent = await SendFrameAsync(frame);
            Log.Debug(
                "{0}.{1}: Sending event {2}/{3} {4}.",
                this, nameof(SendEventAsync), conversationId, methodName, wasSent ? "succeded" : "failed");
        }

        internal Task StartAsync()
        {
            EnsureCorrectState(State.Created);
            duration = Stopwatch.StartNew();
            Task.Run((Func<Task>)ConnectionLoop);
            return startTask.Task;
        }

        private void EnsureCorrectState(State allowedStates, [CallerMemberName] string methodName = "<unknown>")
        {
            if ((state & allowedStates) == 0)
            {
                var message = $"Connection ({this}) is not in the correct state for the requested operation ({methodName}). Current state: {state} Allowed states: {allowedStates}";
                throw new InvalidOperationException(message);
            }
        }

        private ulong AllocateNextConversationId()
        {
            // Interlocked.Add() handles overflow by wrapping, not throwing.
            var newConversationId = Interlocked.Add(ref prevConversationId, 2);
            if (newConversationId < 0)
            {
                throw new EpoxyProtocolErrorException("Exhausted conversation IDs");
            }
            return unchecked((ulong)newConversationId);
        }

        private async Task ConnectionLoop()
        {
            while (true)
            {
                State nextState;

                try
                {
                    if (state == State.Disconnected)
                    {
                        break; // while loop
                    }

                    switch (state)
                    {
                        case State.Created:
                            nextState = DoCreated();
                            break;

                        case State.ClientSendConfig:
                        case State.ServerSendConfig:
                            nextState = await DoSendConfigAsync();
                            break;

                        case State.ClientExpectConfig:
                        case State.ServerExpectConfig:
                            nextState = await DoExpectConfigAsync();
                            break;

                        case State.Connected:
                            // signal after state change to prevent races with
                            // EnsureCorrectState
                            startTask.SetResult(true);
                            nextState = await DoConnectedAsync();
                            break;

                        case State.SendProtocolError:
                            nextState = await DoSendProtocolErrorAsync();
                            break;

                        case State.Disconnecting:
                            nextState = DoDisconnect();
                            break;

                        case State.Disconnected: // we should never enter this switch in the Disconnected state
                        default:
                            Log.Error("Unexpected connection state: {0}", state);
                            protocolError = ProtocolErrorCode.INTERNAL_ERROR;
                            nextState = State.SendProtocolError;
                            break;
                    }
                }
                catch (Exception ex) when (state != State.Disconnecting && state != State.Disconnected)
                {
                    Console.WriteLine("UNHANDLED 1: {0}", ex);

                    Log.Error(ex, "{0}.{1} Unhandled exception. Current state: {2}",
                        this, nameof(ConnectionLoop), state);

                    // we're in a state where we can attempt to disconnect
                    protocolError = ProtocolErrorCode.INTERNAL_ERROR;
                    nextState = State.Disconnecting;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("UNHANDLED 2: {0}", ex);

                    Log.Error(ex, "{0}.{1} Unhandled exception during shutdown. Abandoning connection. Current state: {2}",
                        this, nameof(ConnectionLoop), state);
                    break; // the while loop
                }

                state = nextState;
            } // while (true)

            if (state != State.Disconnected)
            {
                Log.Information("{0}.{1} Abandoning connection. Current state: {2}",
                    this, nameof(ConnectionLoop), state);
            }

            DoDisconnected();
        }

        private State DoCreated()
        {
            State result;

            if (connectionType == ConnectionType.Server)
            {
                var args = new ConnectedEventArgs(this);
                Error disconnectError = parentListener.InvokeOnConnected(args);

                if (disconnectError == null)
                {
                    result = State.ServerExpectConfig;
                }
                else
                {
                    Log.Information("Rejecting connection {0} because {1}:{2}",
                        this, disconnectError.error_code, disconnectError.message);

                    protocolError = ProtocolErrorCode.CONNECTION_REJECTED;
                    errorDetails = disconnectError;
                    result = State.SendProtocolError;
                }
            }
            else
            {
                result = State.ClientSendConfig;
            }

            return result;
        }

        private async Task<State> DoSendConfigAsync()
        {
            Frame emptyConfigFrame = MakeConfigFrame();
            await SendFrameAsync(emptyConfigFrame);
            return (connectionType == ConnectionType.Server ? State.Connected : State.ClientExpectConfig);
        }

        private async Task<State> DoExpectConfigAsync()
        {
            Stream networkStream = netSocket.NetworkStream;
            Frame frame = await Frame.ReadAsync(networkStream, shutdownTokenSource.Token);
            if (frame == null)
            {
                Log.Information("{0}.{1} EOS encountered while waiting for config, so disconnecting.",
                       this, nameof(DoExpectConfigAsync));
                return State.Disconnecting;
            }

            var result = EpoxyProtocol.Classify(frame);
            switch (result.Disposition)
            {
                case EpoxyProtocol.FrameDisposition.ProcessConfig:
                    // we don't actually use the config yet
                    return (connectionType == ConnectionType.Server ? State.ServerSendConfig : State.Connected);

                case EpoxyProtocol.FrameDisposition.HandleProtocolError:
                    // we got a protocol error while we expected config
                    handshakeError = result.Error;
                    return State.Disconnecting;

                case EpoxyProtocol.FrameDisposition.HangUp:
                    return State.Disconnecting;

                default:
                    protocolError = result.ErrorCode ?? ProtocolErrorCode.PROTOCOL_VIOLATED;
                    Log.Error("{0}.{1}: Unsupported FrameDisposition {2} when waiting for config. ErrorCode: {3})",
                        this, nameof(DoExpectConfigAsync), result.Disposition, protocolError);
                    return State.SendProtocolError;
            }
        }

        private async Task<State> DoConnectedAsync()
        {
            while (!shutdownTokenSource.IsCancellationRequested)
            {
                Frame frame;

                try
                {
                    Stream networkStream = netSocket.NetworkStream;
                    frame = await Frame.ReadAsync(networkStream, shutdownTokenSource.Token);
                    if (frame == null)
                    {
                        Log.Information("{0}.{1} EOS encountered, so disconnecting.", this,
                            nameof(DoConnectedAsync));
                        return State.Disconnecting;
                    }
                }
                catch (EpoxyProtocolErrorException pex)
                {
                    Log.Error(pex, "{0}.{1} Protocol error encountered.", this,
                        nameof(DoConnectedAsync));
                    protocolError = ProtocolErrorCode.PROTOCOL_VIOLATED;
                    return State.SendProtocolError;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
                {
                    Log.Error(ex, "{0}.{1} IO error encountered.", this, nameof(DoConnectedAsync));
                    return State.Disconnecting;
                }


                var result = EpoxyProtocol.Classify(frame);
                switch (result.Disposition)
                {
                    case EpoxyProtocol.FrameDisposition.DeliverRequestToService:
                    {
                        State? nextState = DispatchRequest(result.Headers, result.Payload, result.LayerData);
                        if (nextState.HasValue)
                        {
                            return nextState.Value;
                        }
                        else
                        {
                            // continue the read loop
                            break;
                        }
                    }

                    case EpoxyProtocol.FrameDisposition.DeliverResponseToProxy:
                        DispatchResponse(result.Headers, result.Payload, result.LayerData);
                        break;

                    case EpoxyProtocol.FrameDisposition.DeliverEventToService:
                        DispatchEvent(result.Headers, result.Payload, result.LayerData);
                        break;

                    case EpoxyProtocol.FrameDisposition.SendProtocolError:
                        protocolError = result.ErrorCode ?? ProtocolErrorCode.INTERNAL_ERROR;
                        return State.SendProtocolError;

                    case EpoxyProtocol.FrameDisposition.HandleProtocolError:
                    case EpoxyProtocol.FrameDisposition.HangUp:
                        return State.Disconnecting;

                    default:
                        Log.Error("{0}.{1}: Unsupported FrameDisposition {2}", this, nameof(DoConnectedAsync), result.Disposition);
                        protocolError = ProtocolErrorCode.INTERNAL_ERROR;
                        return State.SendProtocolError;
                }
            }

            // shutdown requested between reading frames
            return State.Disconnecting;
        }

        private async Task<State> DoSendProtocolErrorAsync()
        {
            ProtocolErrorCode errorCode = protocolError;
            Error details = errorDetails;

            var frame = MakeProtocolErrorFrame(errorCode, details);
            Log.Debug("{0}.{1}: Sending protocol error with code {2} and details {3}.",
                this, nameof(DoSendProtocolErrorAsync), errorCode, details == null ? "<null>" : details.error_code + details.message);

            bool wasSent = await SendFrameAsync(frame);
            Log.Debug(
                "{0}.{1}: Sending protocol error with code {2} {3}.",
                this, nameof(DoSendProtocolErrorAsync), errorCode, wasSent ? "succeded" : "failed");

            return State.Disconnecting;
        }

        private State DoDisconnect()
        {
            Log.Debug("{0}.{1}: Shutting down.", this, nameof(DoDisconnect));

            netSocket.Shutdown();

            if (connectionType == ConnectionType.Server)
            {
                var args = new DisconnectedEventArgs(this, errorDetails);
                parentListener.InvokeOnDisconnected(args);
            }

            responseMap.Shutdown();

            return State.Disconnected;
        }

        private void DoDisconnected()
        {
            // We signal the start and stop tasks after the state change to
            // prevent races with EnsureCorrectState

            if (handshakeError != null)
            {
                var pex = new EpoxyProtocolErrorException(
                    "Connection was rejected",
                    innerException: null,
                    details: handshakeError.details);
                startTask.TrySetException(pex);
            }
            else
            {
                // the connection got started but then got shutdown shortly after
                startTask.TrySetResult(true);
            }

            stopTask.SetResult(true);

            duration.Stop();
            connectionMetrics.duration_millis = (float) duration.Elapsed.TotalMilliseconds;
            Metrics.Emit(connectionMetrics);
        }

        private State? DispatchRequest(EpoxyHeaders headers, ArraySegment<byte> payload, ArraySegment<byte> layerData)
        {
            if (headers.error_code != (int)ErrorCode.OK)
            {
                Log.Error("{0}.{1}: Received request with a non-zero error code. Conversation ID: {2}",
                    this, nameof(DispatchRequest), headers.conversation_id);
                protocolError = ProtocolErrorCode.PROTOCOL_VIOLATED;
                return State.SendProtocolError;
            }

            IMessage request = Message.FromPayload(Unmarshal.From(payload));

            var receiveContext = new EpoxyReceiveContext(this);

            IBonded bondedLayerData = (layerData.Array == null) ? null : Unmarshal.From(layerData);

            Error layerError = LayerStackUtils.ProcessOnReceive(parentTransport.LayerStack, MessageType.Request, receiveContext, bondedLayerData);

            Task.Run(async () =>
            {
                IMessage result;

                if (layerError == null)
                {
                    result = await serviceHost.DispatchRequest(headers.method_name, receiveContext, request,
                            connectionMetrics);
                }
                else
                {
                    Log.Error("{0}.{1}: Receiving request {2}/{3} failed due to layer error (Code: {4}, Message: {5}).",
                                this, nameof(DispatchRequest), headers.conversation_id, headers.method_name,
                                layerError.error_code, layerError.message);
                    result = Message.FromError(layerError);
                }

                await SendReplyAsync(headers.conversation_id, result);
            });

            // no state change needed
            return null;
        }

        private void DispatchResponse(EpoxyHeaders headers, ArraySegment<byte> payload, ArraySegment<byte> layerData)
        {
            IMessage response;
            if (headers.error_code != (int)ErrorCode.OK)
            {
                response = Message.FromError(Unmarshal<Error>.From(payload));
            }
            else
            {
                response = Message.FromPayload(Unmarshal.From(payload));
            }

            var receiveContext = new EpoxyReceiveContext(this);

            IBonded bondedLayerData = (layerData.Array == null) ? null : Unmarshal.From(layerData);

            Error layerError = LayerStackUtils.ProcessOnReceive(parentTransport.LayerStack, MessageType.Response, receiveContext, bondedLayerData);

            if (layerError != null)
            {
                Log.Error("{0}.{1}: Receiving response {2}/{3} failed due to layer error (Code: {4}, Message: {5}).",
                            this, nameof(DispatchResponse), headers.conversation_id, headers.method_name,
                            layerError.error_code, layerError.message);
                response = Message.FromError(layerError);
            }

            if (!responseMap.Complete(headers.conversation_id, response))
            {
                Log.Error("{0}.{1}: Response for unmatched request. Conversation ID: {2}",
                    this, nameof(DispatchResponse), headers.conversation_id);
            }
        }

        private void DispatchEvent(EpoxyHeaders headers, ArraySegment<byte> payload, ArraySegment<byte> layerData)
        {
            if (headers.error_code != (int)ErrorCode.OK)
            {
                Log.Error("{0}.{1}: Received event with a non-zero error code. Conversation ID: {2}",
                    this, nameof(DispatchEvent), headers.conversation_id);
                return;
            }

            IMessage request = Message.FromPayload(Unmarshal.From(payload));

            var receiveContext = new EpoxyReceiveContext(this);

            IBonded bondedLayerData = (layerData.Array == null) ? null : Unmarshal.From(layerData);

            Error layerError = LayerStackUtils.ProcessOnReceive(parentTransport.LayerStack, MessageType.Event, receiveContext, bondedLayerData);

            if (layerError != null)
            {
                Log.Error("{0}.{1}: Receiving event {2}/{3} failed due to layer error (Code: {4}, Message: {5}).",
                            this, nameof(DispatchEvent), headers.conversation_id, headers.method_name,
                            layerError.error_code, layerError.message);
                return;
            }

            Task.Run(async () =>
            {
                await serviceHost.DispatchEvent(headers.method_name, receiveContext, request, connectionMetrics);
            });
        }

        public override Task StopAsync()
        {
            EnsureCorrectState(State.All);
            shutdownTokenSource.Cancel();
            netSocket.Shutdown();

            return stopTask.Task;
        }

        public async Task<IMessage<TResponse>> RequestResponseAsync<TRequest, TResponse>(string methodName, IMessage<TRequest> message, CancellationToken ct)
        {
            EnsureCorrectState(State.Connected);

            // TODO: cancellation
            IMessage response = await SendRequestAsync(methodName, message);
            return response.Convert<TResponse>();
        }

        public Task FireEventAsync<TPayload>(string methodName, IMessage<TPayload> message)
        {
            EnsureCorrectState(State.Connected);
            return SendEventAsync(methodName, message);
        }

        /// <summary>
        /// Epoxy-private wrapper around <see cref="Socket"/>. Provides idempotent shutdown.
        /// </summary>
        private class EpoxySocket
        {
            Socket socket;
            NetworkStream stream;
            int isShutdown;

            public EpoxySocket(Socket sock)
            {
                socket = sock;
                stream = new NetworkStream(sock, ownsSocket: false);
                WriteLock = new SemaphoreSlim(1, 1);
                isShutdown = 0;
            }

            public Stream NetworkStream
            {
                get
                {
                    if (isShutdown != 0)
                    {
                        throw new ObjectDisposedException(nameof(EpoxySocket));
                    }

                    return stream;
                }
            }

            // It looks like we don't need to .Dispose this SemaphoreSlim. The
            // current implementation of SemaphoreSlim only does interesting
            // stuff during .Dispose if there's an allocated
            // AvailableWaitHandle. We never call that, so there shouldn't be
            // anything needing disposal. If we do end up allocating a wait
            // handle somehow, its finalizer will save us.
            public SemaphoreSlim WriteLock { get; }

            public void Shutdown()
            {
                int oldIsShutdown = Interlocked.CompareExchange(ref isShutdown, 1, 0);
                if (oldIsShutdown == 0)
                {
                    // we are responsible for shutdown
                    try
                    {
                        stream.Dispose();

                        try
                        {
                            socket.Shutdown(SocketShutdown.Send);
                            socket.Close();
                        }
                        catch (ObjectDisposedException)
                        {
                            // ignore, as we're shutting down anyway
                        }

                        // We cannot call socket.Disconnect, as that will block
                        // for longer than we want. So, we just forcible close
                        // the socket with Dispose
                        socket.Dispose();
                    }
                    catch (Exception ex) when (ex is IOException || ex is SocketException)
                    {
                        Log.Error(ex, "Exception during connection shutdown");
                    }

                    stream = null;
                    socket = null;
                }
            }
        }
    }
}
