using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Kayak
{
    class DefaultKayakSocket : ISocket
    {
        internal ISocketDelegate del;

        public int ID { get; private set; }
        private static int s_NextId;

        public IPEndPoint RemoteEndPoint { get { return (IPEndPoint)m_Socket.RemoteEndPoint; } }

        private OutputBuffer m_Buffer;

        private byte[] m_InputBuffer;

        private KayakSocketState m_State;

        private ISocketWrapper m_Socket;
        private Action m_Continuation;
        private IScheduler m_Scheduler;

        private readonly object m_LockObject = new object();

        internal DefaultKayakSocket(ISocketDelegate del, IScheduler scheduler)
        {
            this.m_Scheduler = scheduler;
            this.del = del;
            m_State = new KayakSocketState(true);
        }

        internal DefaultKayakSocket(ISocketWrapper socket, IScheduler scheduler)
        {
            this.ID = Interlocked.Increment(ref s_NextId);
            this.m_Socket = socket;
            this.m_Scheduler = scheduler;
            m_State = new KayakSocketState(false);
        }

        public void Connect(IPEndPoint ep)
		{
            m_State.SetConnecting();

            Debug.WriteLine("KayakSocket: connecting to " + ep);
            this.m_Socket = new SocketWrapper(ep.Address.AddressFamily);
			
            m_Socket.BeginConnect(ep, iasr => 
            {
            	Debug.WriteLine("KayakSocket: connected to " + ep);
                Exception error = null;

                try
                {
                    m_Socket.EndConnect(iasr);
                }
                catch (Exception e)
                {
                    error = e;
                }
                
                m_Scheduler.Post(() =>
                {
                    if (error is ObjectDisposedException)
                        return;

                    if (error != null)
                    {
                        m_State.SetError();

                        Debug.WriteLine("KayakSocket: error while connecting to " + ep);
                        RaiseError(error);
                    }
                    else
                    {
                        m_State.SetConnected();

                        Debug.WriteLine("KayakSocket: connected to " + ep);

                        del.OnConnected(this);

                        BeginRead();
                    }
                });
            });
        }

        internal void BeginRead()
        {
            if (m_InputBuffer == null)
                m_InputBuffer = new byte[1024 * 4];

            while (true)
            {
                if (!m_State.CanRead()) return;

                int read;
                Exception error;
                IAsyncResult ar0 = null;

                Debug.WriteLine("KayakSocket: reading.");

                try
                {
                    ar0 = m_Socket.BeginReceive(m_InputBuffer, 0, m_InputBuffer.Length, ar =>
                    {
                        if (ar.CompletedSynchronously) return;

                        read = EndRead(ar, out error);
						
						// small optimization
                        if (error is ObjectDisposedException)
                            return;

                        m_Scheduler.Post(() =>
                        {
                            Debug.WriteLine("KayakSocket: receive completed async");

                            if (error != null)
                            {
                                HandleReadError(error);
                            }
                            else
                            {
                                if (!HandleReadResult(read, false))
                                    BeginRead();
                            }
                        });
                    });
                }
                catch (Exception e)
                {
                    HandleReadError(e);
                    break;
                }

                if (!ar0.CompletedSynchronously)
                    break;

                Debug.WriteLine("KayakSocket: receive completed sync");
                read = EndRead(ar0, out error);

                if (error != null)
                {
                    HandleReadError(error);
                    break;
                }
                else
                {
                    if (HandleReadResult(read, true))
                        break;
                }
            }
        }

        int EndRead(IAsyncResult ar, out Exception error)
        {
            error = null;
            try
            {
                return m_Socket.EndReceive(ar);
            }
            catch (Exception e)
            {
                error = e;
                return -1;
            }
        }

        bool HandleReadResult(int read, bool sync)
        {
            Debug.WriteLine("KayakSocket: " + (sync ? "" : "a") + "sync read " + read);

            if (read == 0)
            {
                PeerHungUp();
                return false;
            }
            else
            {
                return del.OnData(this, new ArraySegment<byte>(m_InputBuffer, 0, read), BeginRead);
            }
        }

        void HandleReadError(Exception e)
        {
            if (e is ObjectDisposedException)
                return;

            Debug.WriteLine("KayakSocket: read error");

            if (e is SocketException)
            {
                var socketException = e as SocketException;

                if (socketException.ErrorCode == 10053 || socketException.ErrorCode == 10054)
                {
                    Debug.WriteLine("KayakSocket: peer reset (" + socketException.ErrorCode + ")");
                    PeerHungUp();
                    return;
                }
            }

            m_State.SetError();

            RaiseError(new Exception("Error while reading.", e));
        }

        void PeerHungUp()
        {
            Debug.WriteLine("KayakSocket: peer hung up.");
            bool raiseClosed = false;
            m_State.SetReadEnded(out raiseClosed);

            del.OnEnd(this);

            if (raiseClosed)
                RaiseClosed();
        }

        public bool Write(ArraySegment<byte> data, Action continuation)
        {
            lock (m_LockObject)
            {
                m_State.BeginWrite(data.Count > 0);

                if (data.Count == 0) return false;

                if (this.m_Continuation != null)
                    throw new InvalidOperationException("Write was pending.");

                if (m_Buffer == null)
                    m_Buffer = new OutputBuffer();

                var bufferSize = m_Buffer.Size;

                // XXX copy! could optimize here?
                m_Buffer.Add(data);
                Debug.WriteLine("KayakSocket: added " + data.Count + " bytes to buffer, buffer size was " + bufferSize + ", buffer size is " + m_Buffer.Size);

                if (bufferSize > 0)
                {
                    // we're between an async beginsend and endsend,
                    // and user did not provide continuation

                    if (continuation != null)
                    {
                        this.m_Continuation = continuation;
                        return true;
                    }
                    else
                        return false;
                }
                else
                {
                    var result = BeginSend();

                    // tricky: potentially throwing away fact that send will complete async
                    if (continuation == null)
                        result = false;

                    if (result)
                        this.m_Continuation = continuation;

                    return result;
                }
            }
        }

        bool BeginSend()
        {
            lock (m_LockObject)
            {
                while (true)
                {
                    if (BufferIsEmpty())
                        break;

                    int written = 0;
                    Exception error;
                    IAsyncResult ar0;

                    try
                    {
                        ar0 = m_Socket.BeginSend(m_Buffer.Data, ar =>
                        {
                            if (ar.CompletedSynchronously)
                                return;

                            written = EndSend(ar, out error);

                            // small optimization
                            if (error is ObjectDisposedException)
                                return;

                            m_Scheduler.Post(() =>
                            {
                                if (error != null)
                                    HandleSendError(error);
                                else
                                    HandleSendResult(written, false);

                                if (!BeginSend() && m_Continuation != null)
                                {
                                    var c = m_Continuation;
                                    m_Continuation = null;
                                    c();
                                }
                            });
                        });
                    }
                    catch (Exception e)
                    {
                        HandleSendError(e);
                        break;
                    }

                    if (!ar0.CompletedSynchronously)
                        return true;

                    written = EndSend(ar0, out error);

                    if (error != null)
                    {
                        HandleSendError(error);
                        break;
                    }
                    else
                        HandleSendResult(written, true);
                }

                return false;
            }
        }

        int EndSend(IAsyncResult ar, out Exception error)
        {
            error = null;
            try
            {
                return m_Socket.EndSend(ar);
            }
            catch (Exception e)
            {
                error = e;
                return -1;
            }
        }

        void HandleSendResult(int written, bool sync)
        {
            m_Buffer.Remove(written);

            Debug.WriteLine("KayakSocket: Wrote " + written + " " + (sync ? "" : "a") + "sync, buffer size is " + m_Buffer.Size);

            bool shutdownSocket = false;
            bool raiseClosed = false;

            m_State.EndWrite(BufferIsEmpty(), out shutdownSocket, out raiseClosed);

            if (shutdownSocket)
            {
                Debug.WriteLine("KayakSocket: shutting down socket after send.");
                m_Socket.Shutdown();
            }

            if (raiseClosed)
                RaiseClosed();
        }

        void HandleSendError(Exception error)
        {
			if (error is ObjectDisposedException) return;
			
            m_State.SetError();
            RaiseError(new Exception("Exception on write.", error));
        }

        public void End()
        {
            Debug.WriteLine("KayakSocket: end");

            bool shutdownSocket = false;
            bool raiseClosed = false;
            
            m_State.SetEnded(out shutdownSocket, out raiseClosed);

            if (shutdownSocket)
            {
                Debug.WriteLine("KayakSocket: shutting down socket on End.");
                m_Socket.Shutdown();
            }

            if (raiseClosed)
            {
                RaiseClosed();
            }
        }

        public void Dispose()
        {
            m_State.SetDisposed();

            if (m_Socket != null) // i. e., never connected
                m_Socket.Dispose();
        }

        bool BufferIsEmpty()
        {
            return m_Socket != null && (m_Buffer == null || m_Buffer.Size == 0);
        }

        void RaiseError(Exception e)
        {
            Debug.WriteLine("KayakSocket: raising OnError");
            del.OnError(this, e);

            RaiseClosed();
        }

        void RaiseClosed()
        {
            Debug.WriteLine("KayakSocket: raising OnClose");
            del.OnClose(this);
        }
    }
}
