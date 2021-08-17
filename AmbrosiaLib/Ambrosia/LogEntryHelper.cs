using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DebuggingSupport;
using static Ambrosia.StreamCommunicator;

namespace Ambrosia
{
    public class LogEntryHelper
    {

        /// <summary>
        /// Takes a log entry message (which may include multiple RPC-events)
        /// and splits this message into separate messages.
        /// Each new message represents a single event.
        /// </summary>
        /// <param name="logEntryMessage">The message to split (as this represents the log record content it might include multiple actual messages)</param>
        /// <returns></returns>
        public static async IAsyncEnumerable<Message> SplitLogEntryMessage(Message logEntryMessage)
        {
            var _inputFlexBuffer = new FlexReadBuffer();
            var logEntryStream = new MemoryStream(logEntryMessage.Content);
            
            var bytesToRead = logEntryMessage.Length;
            while (bytesToRead > 0)
            {
                await FlexReadBuffer.DeserializeAsync(logEntryStream, _inputFlexBuffer);
                bytesToRead -= _inputFlexBuffer.Length;
                var _cursor = _inputFlexBuffer.LengthLength; // this way we don't need to compute how much space was used to represent the length of the buffer.
                var firstByte = _inputFlexBuffer.Buffer[_cursor];

                switch (firstByte)
                {
                    case AmbrosiaRuntimeLBConstants.InitalMessageByte:
                    case AmbrosiaRuntimeLBConstants.checkpointByte:
                    case AmbrosiaRuntimeLBConstants.takeCheckpointByte:
                    case AmbrosiaRuntimeLBConstants.becomingPrimaryByte:
                    case AmbrosiaRuntimeLBConstants.takeBecomingPrimaryCheckpointByte:
                    case AmbrosiaRuntimeLBConstants.upgradeTakeCheckpointByte:
                    case AmbrosiaRuntimeLBConstants.upgradeServiceByte:
                    case AmbrosiaRuntimeLBConstants.takeSubCheckpointByte:
                    case AmbrosiaRuntimeLBConstants.RPCByte:
                        // Un-batchable messages -> they only contain a single event
                        // BUT: We might have more than one message within a single Log Record -> We need to return them as separate events!
                        var singleMessage = new AmbrosiaMessage();
                        singleMessage.Length = _inputFlexBuffer.Length;
                        singleMessage.Content = new byte[singleMessage.Length];
                        Buffer.BlockCopy(_inputFlexBuffer.Buffer, 0, singleMessage.Content, 0, singleMessage.Length);
                        yield return singleMessage;
                        break;
                    case AmbrosiaRuntimeLBConstants.RPCBatchByte:
                    case AmbrosiaRuntimeLBConstants.CountReplayableRPCBatchByte:
                        // Batched messages -> process them!
                        var numberOfRPCs = 1;
                        var lengthOfCurrentRPC = 0;
                        
                        _cursor++;
                        numberOfRPCs = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                        _cursor += IntSize(numberOfRPCs);
                        if (firstByte == AmbrosiaRuntimeLBConstants.CountReplayableRPCBatchByte)
                        {
                            var numReplayableRPCs = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                            _cursor += IntSize(numReplayableRPCs);
                        }
                        
                        // Iterate over all messages within this batch:
                        for (int i = 0; i < numberOfRPCs; i++)
                        {
                            if (numberOfRPCs == 1)
                            {
                                // If this batch only contains a single message -> send it!
                                yield return logEntryMessage;
                                break;
                            }
                            
                            if (1 < numberOfRPCs)
                            {
                                lengthOfCurrentRPC = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                _cursor += IntSize(lengthOfCurrentRPC);
                            }
                            
                            var shouldBeRPCByte = _inputFlexBuffer.Buffer[_cursor];
                            if (shouldBeRPCByte != AmbrosiaRuntimeLBConstants.RPCByte)
                            {
                                Console.WriteLine("UNKNOWN BYTE: {0}!!", shouldBeRPCByte);
                                throw new Exception("Illegal leading byte in message");
                            }

                            var rpcContent = new byte[IntSize(lengthOfCurrentRPC) + lengthOfCurrentRPC];
                            Buffer.BlockCopy(_inputFlexBuffer.Buffer, _cursor, rpcContent, IntSize(lengthOfCurrentRPC), lengthOfCurrentRPC);
                            rpcContent.WriteInt(0, lengthOfCurrentRPC);
                            
                            var message = new AmbrosiaMessage();
                            message.Content = rpcContent;
                            message.Length = message.Content.Length;

                            _cursor += lengthOfCurrentRPC; // Move position to next message
                            yield return message;
                        }
                        break;
                    default:
                    {
                        var s = $"Illegal leading byte in message: {firstByte}";
#if DEBUG
                        Trace.TraceWarning(s);
#endif
                        yield return logEntryMessage;
                        yield break; // If the message was "faulty" we might as well return it completely (as we do not know how to handle it)
                    }
                }
                
                // Reset buffer to load next message/event from log record
                _inputFlexBuffer.ResetBuffer();
            }
        }

        public static async Task<Event> ExtractEventAsync(Message message, long timestamp)
        {
            var _inputFlexBuffer = new FlexReadBuffer();
            await FlexReadBuffer.DeserializeAsync(new MemoryStream(message.Content), _inputFlexBuffer);
            var _cursor = _inputFlexBuffer.LengthLength; // this way we don't need to compute how much space was used to represent the length of the buffer.
            var firstByte = _inputFlexBuffer.Buffer[_cursor];
            
            if (firstByte != AmbrosiaRuntimeLBConstants.RPCByte)
            {
                // No RPC -> No Event! (Dummy-Event to symbolize that there was an event but not a relevant one)
                return new AmbrosiaEvent(timestamp, EventType.Internal, -1);
            }

            _cursor++;
            var returnValueType = (ReturnValueTypes)_inputFlexBuffer.Buffer[_cursor++];
            if (returnValueType != ReturnValueTypes.None) // receiving a return value
            {
                // No "real" RPC but only a response -> No Event!
                return null;
            }

            var methodId = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
            _cursor += IntSize(methodId);
            var _rpcType = (RpcTypes.RpcType)_inputFlexBuffer.Buffer[_cursor++];
            var rpcType = _rpcType is RpcTypes.RpcType.ReturnValue ? EventType.External :
                _rpcType is RpcTypes.RpcType.FireAndForget ? EventType.External :
                _rpcType is RpcTypes.RpcType.Impulse ? EventType.Internal :
                throw new ArgumentOutOfRangeException();

            if (!_rpcType.IsFireAndForget())
            {
                // read return address and sequence number
                var senderOfRPCLength = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                var sizeOfSender = IntSize(senderOfRPCLength);
                _cursor += sizeOfSender;
                _cursor += senderOfRPCLength;
                var sequenceNumber = _inputFlexBuffer.Buffer.ReadBufferedLong(_cursor);
                _cursor += LongSize(sequenceNumber);
            }
            
            var lengthOfSerializedArguments = message.Content.Length - _cursor;
            byte[] serializedMethods = new byte[lengthOfSerializedArguments];
            Buffer.BlockCopy(_inputFlexBuffer.Buffer, _cursor, serializedMethods, 0, lengthOfSerializedArguments);

            return new AmbrosiaEvent(timestamp, rpcType, methodId, serializedMethods);
        }
    }
}