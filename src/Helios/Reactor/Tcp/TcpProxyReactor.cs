﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Helios.Buffers;
using Helios.Exceptions;
using Helios.Net;
using Helios.Reactor.Response;
using Helios.Serialization;
using Helios.Topology;

namespace Helios.Reactor.Tcp
{
    /// <summary>
    /// High-performance TCP reactor that uses a single buffer and manages all client connections internally.
    /// 
    /// Passes <see cref="ReactorProxyResponseChannel"/> instances to connected clients and allows them to set up their own event loop behavior.
    /// 
    /// All I/O is still handled internally through the proxy reactor.
    /// </summary>
    public class TcpProxyReactor : ProxyReactorBase<Socket>
    {
        public TcpProxyReactor(IPAddress localAddress, int localPort, NetworkEventLoop eventLoop, IMessageEncoder encoder, IMessageDecoder decoder, IByteBufAllocator allocator, int bufferSize = NetworkConstants.DEFAULT_BUFFER_SIZE) 
            : base(localAddress, localPort, eventLoop, encoder, decoder, allocator, SocketType.Stream, ProtocolType.Tcp, bufferSize)
        {
            LocalEndpoint = new IPEndPoint(localAddress, localPort);
            Listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Buffer = new byte[bufferSize];
        }

        public override bool IsActive { get; protected set; }
        public override void Configure(IConnectionConfig config)
        {
            if (config.HasOption<int>("receiveBufferSize"))
                Listener.ReceiveBufferSize = config.GetOption<int>("receiveBufferSize");
            if (config.HasOption<int>("sendBufferSize"))
                Listener.SendBufferSize = config.GetOption<int>("sendBufferSize");
            if (config.HasOption<bool>("reuseAddress"))
                Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, config.GetOption<bool>("reuseAddress"));
            if (config.HasOption<int>("backlog"))
                Backlog = config.GetOption<int>("backlog");
            if (config.HasOption<bool>("tcpNoDelay"))
                Listener.NoDelay = config.GetOption<bool>("tcpNoDelay");
            if (config.HasOption<bool>("keepAlive"))
                Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, config.GetOption<bool>("keepAlive"));
            if(config.HasOption<bool>("linger") && config.GetOption<bool>("linger"))
                Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 10));
            else
                Listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);

        }

        protected override void StartInternal()
        {
            IsActive = true;
            Listener.Listen(Backlog);
            Listener.BeginAccept(AcceptCallback, null);
        }

        private void AcceptCallback(IAsyncResult ar)
        {
            var newSocket = Listener.EndAccept(ar);
            var node = NodeBuilder.FromEndpoint((IPEndPoint) newSocket.RemoteEndPoint);
            NodeMap.Add(newSocket, node);
            var responseChannel = new ReactorProxyResponseChannel(this, newSocket, EventLoop);
            SocketMap.Add(node, responseChannel);
            NodeConnected(node, responseChannel);
            newSocket.BeginReceive(Buffer, 0, Buffer.Length, SocketFlags.None, ReceiveCallback, newSocket);
            Listener.BeginAccept(AcceptCallback, null); //accept more connections
        }

        private void ReceiveCallback(IAsyncResult ar)
        {
            var socket = (Socket)ar.AsyncState;
            try
            {
                var received = socket.EndReceive(ar);
                var dataBuff = new byte[received];
                Array.Copy(Buffer, dataBuff, received);
                var networkData = new NetworkData() {Buffer = dataBuff, Length = received, RemoteHost = NodeMap[socket]};
                socket.BeginReceive(Buffer, 0, Buffer.Length, SocketFlags.None, ReceiveCallback, socket);
                    //receive more messages
                var adapter = SocketMap[NodeMap[socket]];
                ReceivedData(networkData, adapter);
            }
            catch (SocketException ex) //node disconnected
            {
                var node = NodeMap[socket];
                var connection = SocketMap[node];
                CloseConnection(ex, connection);
            }
            catch (Exception ex)
            {
                 var node = NodeMap[socket];
                var connection = SocketMap[node];
                OnErrorIfNotNull(ex, connection);
            }
        }

        protected override void StopInternal()
        {
        }

        public override void Send(NetworkData data)
        {
            var clientSocket = SocketMap[data.RemoteHost];
            List<NetworkData> encoded;
            Encoder.Encode(data, out encoded);
            foreach (var message in encoded)
               clientSocket.Socket.BeginSend(message.Buffer, 0, message.Length, SocketFlags.None, SendCallback, clientSocket.Socket);
        }

        internal override void CloseConnection(Exception ex, IConnection remoteHost)
        {
            if (!SocketMap.ContainsKey(remoteHost.RemoteHost)) return; //already been removed

            var clientSocket = SocketMap[remoteHost.RemoteHost];

            try
            {
                if (clientSocket.Socket.Connected)
                {
                    clientSocket.Socket.Close();
                    NodeDisconnected(new HeliosConnectionException(ExceptionType.Closed, ex), remoteHost);
                }
            }
            finally
            {
                NodeMap.Remove(clientSocket.Socket);
                SocketMap.Remove(remoteHost.RemoteHost);
                clientSocket.Dispose();
            }
        }

        internal override void CloseConnection(IConnection remoteHost)
        {
           CloseConnection(null, remoteHost);
        }

        private void SendCallback(IAsyncResult ar)
        {
            var socket = (Socket) ar.AsyncState;
            try
            {
                socket.EndSend(ar);
            }
            catch (SocketException ex) //node disconnected
            {
                var node = NodeMap[socket];
                var connection = SocketMap[node];
                CloseConnection(ex, connection);
            }
            catch (Exception ex)
            {
                var node = NodeMap[socket];
                var connection = SocketMap[node];
                OnErrorIfNotNull(ex, connection);
            }
        }

        #region IDisposable Members

        public override void Dispose(bool disposing)
        {
            if (!WasDisposed && disposing && Listener != null)
            {
                Stop();
                Listener.Dispose();
            }
            IsActive = false;
            WasDisposed = true;
        }

        #endregion
    }

    public class TcpSingleEventLoopProxyReactor : TcpProxyReactor
    {
        public TcpSingleEventLoopProxyReactor(IPAddress localAddress, int localPort, NetworkEventLoop eventLoop, IMessageEncoder encoder, IMessageDecoder decoder,IByteBufAllocator allocator, int bufferSize = NetworkConstants.DEFAULT_BUFFER_SIZE) : base(localAddress, localPort, eventLoop, encoder, decoder, allocator, bufferSize)
        {
        }

        protected override void ReceivedData(NetworkData availableData, ReactorResponseChannel responseChannel)
        {
            if (EventLoop.Receive != null)
            {
                List<NetworkData> decoded;
                Decoder.Decode(availableData, out decoded);
                foreach(var message in decoded)
                    EventLoop.Receive(message, responseChannel);
            }
        }
    }
}
