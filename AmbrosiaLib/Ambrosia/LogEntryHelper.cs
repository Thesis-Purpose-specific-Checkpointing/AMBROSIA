using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        /// <param name="logEntryMessage">The message to split</param>
        /// <returns></returns>
        public static async IAsyncEnumerable<Message> SplitLogEntryMessage(Message logEntryMessage)
        {
            var _inputFlexBuffer = new FlexReadBuffer();
            
            await FlexReadBuffer.DeserializeAsync(new MemoryStream(logEntryMessage.Content), _inputFlexBuffer);

            var bytesToRead = logEntryMessage.Length;
            while (bytesToRead > 0)
            { 
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
                        // Un-batchable messages -> return them and end the processing!
                        yield return logEntryMessage;
                        yield break;
                    case AmbrosiaRuntimeLBConstants.RPCByte:
                        yield return logEntryMessage;
                        yield break;
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

                        yield break;
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
            }

            _inputFlexBuffer.ResetBuffer();
        }
    }
}