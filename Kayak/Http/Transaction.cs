﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HttpMachine;
using System.Diagnostics;

namespace Kayak.Http
{
    class HttpServerDelegate : IServerDelegate
    {
        Func<IRequestDelegate> delegateFactory;

        public HttpServerDelegate(Func<IRequestDelegate> delegateFactory)
        {
            this.delegateFactory = delegateFactory;
        }

        public void OnConnection(IServer server, ISocket socket)
        {
            socket.Delegate = new HttpSocketDelegate(socket, new Transaction2(delegateFactory(), (v, k) => new Response(socket, v, k)));
        }

        public void OnClose(IServer server)
        {
        }
    }

    class HttpSocketDelegate : ISocketDelegate
    {
        HttpParser parser;
        ParserHandler handler;
        ISocket socket;
        Transaction2 eventHandler;

        public HttpSocketDelegate(ISocket socket, Transaction2 eventHandler)
        {
            this.socket = socket;
            this.eventHandler = eventHandler;
            handler = new ParserHandler();
            parser = new HttpParser(handler);
        }

        public bool OnData(ISocket socket, ArraySegment<byte> data, Action continuation)
        {
            if (parser.Execute(data) != data.Count)
            {
                Trace.Write("Error while parsing request.");
                socket.Dispose();
            }

            while (handler.HasEvents)
            {
                if (eventHandler.Execute(handler.GetNextEvent(), continuation))
                    return true;
            }

            return false;
        }

        public void OnTimeout(ISocket socket)
        {
            OnError(socket, new Exception("Socket timeout"));
        }

        public void OnError(ISocket socket, Exception e)
        {
            //requestDelegate.OnError(request, e);
            Debug.WriteLine("Error on socket.");
            e.PrintStacktrace();
            socket.Dispose();
        }

        public void OnEnd(ISocket socket) {
            OnData(socket, default(ArraySegment<byte>), null);
            socket.End();
        }
        public void OnClose(ISocket socket) { }
        public void OnConnected(ISocket socket) { }
    }

    class Transaction2
    {
        enum TransactionState
        {
            NewMessage,
            MessageBegan,
            MessageBody,
            MessageFinishing,
            Dead
        }

        bool keepAlive;
        Func<Version, bool, IResponse> responseFactory;
        TransactionState state;
        IRequest request;
        IRequestDelegate requestDelegate;

        public Transaction2(IRequestDelegate requestDelegate, Func<Version, bool, IResponse> responseFactory)
        {
            this.requestDelegate = requestDelegate;
            this.responseFactory = responseFactory;
            keepAlive = true;
        }

        public bool Execute(HttpRequestEvent httpEvent, Action continuation)
        {
            if (state == TransactionState.Dead)
            {
                throw new Exception("Transaction is dead, expected no more events.");
            }

            switch (state)
            {
                case TransactionState.NewMessage:
                    Debug.WriteLine("Entering TransactionState.NewMessage");
                    if (httpEvent.Type == HttpRequestEventType.RequestHeaders)
                    {
                        keepAlive = httpEvent.KeepAlive;
                        request = httpEvent.Request;

                        var response = responseFactory(httpEvent.Request.Version, keepAlive);

                        requestDelegate.OnStart(httpEvent.Request, response);

                        state = TransactionState.MessageBegan;

                        break;
                    }
                    throw new Exception("Got unexpected state: " + httpEvent.Type);

                case TransactionState.MessageBegan:
                    Debug.WriteLine("Entering TransactionState.MessageBegan");
                    if (httpEvent.Type == HttpRequestEventType.RequestBody)
                    {
                        state = TransactionState.MessageBody;
                        goto case TransactionState.MessageBody;
                    }

                    if (httpEvent.Type == HttpRequestEventType.RequestEnded)
                    {
                        state = TransactionState.MessageFinishing;
                        goto case TransactionState.MessageFinishing;
                    }
                    throw new Exception("Got unexpected state: " + httpEvent.Type);

                case TransactionState.MessageBody:
                    if (httpEvent.Type == HttpRequestEventType.RequestBody)
                    {
                        requestDelegate.OnBody(httpEvent.Data, continuation);

                        // XXX for now this logic is deferred to the request delegate:
                        // if 
                        // - no expect-continue
                        // - application ended response
                        // - "request includes request body"
                        // then 
                        //   read and discard remainder of incoming message and goto PreRequest
                        return false;
                    }
                    else if (httpEvent.Type == HttpRequestEventType.RequestEnded)
                    {
                        state = TransactionState.MessageFinishing;
                        goto case TransactionState.MessageFinishing;
                    }

                    throw new Exception("Got unexpected state: " + httpEvent.Type);

                case TransactionState.MessageFinishing:
                    Debug.WriteLine("Entering TransactionState.MessageFinishing");
                    if (httpEvent.Type == HttpRequestEventType.RequestEnded)
                    {
                        requestDelegate.OnEnd();

                        state = keepAlive ? TransactionState.NewMessage : TransactionState.Dead;
                        break;
                    }
                    throw new Exception("Got unexpected state: " + httpEvent.Type);

                default:
                    throw new Exception("Unhandled state " + httpEvent.Type);
            }

            return true;
        }
    }
}