﻿// 
// Copyright 2013-2014 Hans Wolff
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// 

using RedFoxMQ.Transports;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RedFoxMQ
{
    class MessageReceiveLoop : IDisposable
    {
        private readonly MessageFrameReceiver _messageFrameReceiver;

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private TaskCompletionSource<bool> _started = new TaskCompletionSource<bool>();
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(true);

        private readonly IMessageSerialization _messageSerialization;
        private readonly ISocket _socket;
        private readonly MessageReceivedDelegate _messageReceivedDelegate;
        private readonly SocketExceptionDelegate _socketExceptionDelegate;

        public MessageReceiveLoop(IMessageSerialization messageSerialization, ISocket socket, 
            MessageReceivedDelegate messageReceivedDelegate, SocketExceptionDelegate onExceptionDelegate)
        {
            if (messageSerialization == null) throw new ArgumentNullException("messageSerialization");
            if (socket == null) throw new ArgumentNullException("socket");
            if (messageReceivedDelegate == null) throw new ArgumentNullException("messageReceivedDelegate");

            _socket = socket;
            _messageFrameReceiver = new MessageFrameReceiver(socket);
            _messageSerialization = messageSerialization;
            _messageReceivedDelegate = messageReceivedDelegate;
            _socketExceptionDelegate = onExceptionDelegate;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();

            _started = new TaskCompletionSource<bool>();
            StartReceiveLoop();
            _started.Task.Wait();
        }

        public void Stop(bool waitForExit = true)
        {
            _cts.Cancel(false);

            if (waitForExit) _stopped.Wait();
        }

        private void StartReceiveLoop()
        {
            Task.Factory.StartNew(() => ReceiveLoop(_cts.Token), TaskCreationOptions.LongRunning);
        }

        private void ReceiveLoop(CancellationToken cancellationToken)
        {
            _stopped.Reset();
            _started.SetResult(true);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var messageFrame = _messageFrameReceiver.Receive();

                    IMessage message = null;
                    try
                    {
                        message = _messageSerialization.Deserialize(
                            messageFrame.MessageTypeId,
                            messageFrame.RawMessage);
                    }
                    catch (RedFoxBaseException ex)
                    {
                        Debug.WriteLine(ex);

                        _socketExceptionDelegate(_socket, ex);
                    }

                    FireMessageReceivedEvent(message);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException ex)
            {
                _socketExceptionDelegate(_socket, ex);
            }
            finally
            {
                _stopped.Set();
            }
        }

        private void FireMessageReceivedEvent(IMessage message)
        {
            if (message == null) return;

            try { _messageReceivedDelegate(_socket, message); }
            catch { }
        }

        #region Dispose
        private bool _disposed;
        private readonly object _disposeLock = new object();

        protected virtual void Dispose(bool disposing) 
        {
            lock (_disposeLock)
            {
                if (!_disposed)
                {
                    _messageFrameReceiver.Disconnect();
                    Stop(false);

                    _disposed = true;
                    if (disposing) GC.SuppressFinalize(this);
                }
            }
        }

        public void Dispose()
        {
            Dispose(false);
        }

        ~MessageReceiveLoop()
        {
            Dispose(false);
        }
        #endregion

    }
}
