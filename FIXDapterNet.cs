using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FIXDapterNet
{
    public class FIXDapterNet
    {
        private TcpClient _tcpClient;

        private CancellationTokenSource _cts;


        private int _outboundSeqNum = 1;  
        private int _expectedInboundSeqNum = 1;



        public async Task ConnectAsync(string host, int port)
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(host, port);


            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public async Task SendFixMessageAsync(Dictionary<int, string> tags)
        {
            if (_tcpClient == null || !_tcpClient.Connected)
                throw new InvalidOperationException("Client not connected.");

            tags[8] = tags.ContainsKey(8) ? tags[8] : "FIX.4.2";        
            tags[34] = _expectedInboundSeqNum+1.ToString();                      
            tags[52] = DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff"); 

            var bodyTags = tags.Where(kvp => kvp.Key != 8 && kvp.Key != 9 && kvp.Key != 10);
            string body = string.Join("\x01", bodyTags.Select(kvp => $"{kvp.Key}={kvp.Value}")) + "\x01";

            string beginString = tags[8];
            string bodyLength = Encoding.ASCII.GetByteCount(body).ToString();

            string messageWithoutChecksum = $"8={beginString}\x019={bodyLength}\x01{body}";
            int checksum = ComputeFixChecksum(messageWithoutChecksum);
            string finalMessage = $"{messageWithoutChecksum}10={checksum:000}\x01";

            byte[] bytes = Encoding.ASCII.GetBytes(finalMessage);
            await _tcpClient.GetStream().WriteAsync(bytes, 0, bytes.Length);


        }
        public void Disconnect()
        {
            _cts?.Cancel();

            _tcpClient?.Close();
        }



       

        public event EventHandler<(Dictionary<int, string> Tags, long Timestamp)> ParsedMessageReceived;

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var stream = _tcpClient.GetStream();
            byte[] buffer = new byte[8192];
            byte[] messageBuffer = new byte[65536];
            int bufferLength = 0;

            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                long receiveTimestamp = Stopwatch.GetTimestamp();

                if (bytesRead == 0) break;

                Buffer.BlockCopy(buffer, 0, messageBuffer, bufferLength, bytesRead);
                bufferLength += bytesRead;

                int offset = 0;

                while (offset + 10 < bufferLength)
                {
                    if (messageBuffer[offset] != (byte)'8' || messageBuffer[offset + 1] != (byte)'=')
                    {
                        offset++;
                        continue;
                    }

                    int i = offset + 2;
                    int lenTag = -1;
                    while (i + 1 < bufferLength)
                    {
                        if (messageBuffer[i] == (byte)'9' && messageBuffer[i + 1] == (byte)'=')
                        {
                            lenTag = i;
                            break;
                        }
                        i++;
                    }
                    if (lenTag == -1) break;

                    int lenValueStart = lenTag + 2;
                    int lenValueEnd = lenValueStart;
                    while (lenValueEnd < bufferLength && messageBuffer[lenValueEnd] != 1)
                        lenValueEnd++;

                    if (lenValueEnd == bufferLength) break;

                    if (!TryParseAsciiInt(messageBuffer, lenValueStart, lenValueEnd, out int bodyLength))
                    {
                        offset++;
                        continue;
                    }

                    int bodyStart = lenValueEnd + 1;
                    int fullMessageEnd = bodyStart + bodyLength + 7; 

                    if (bufferLength < fullMessageEnd) break;

                    var rawSlice = new ArraySegment<byte>(messageBuffer, offset, fullMessageEnd - offset);

                    string rawString = Encoding.ASCII.GetString(rawSlice.Array, rawSlice.Offset, rawSlice.Count);
                    var tags = new Dictionary<int, string>();

                    foreach (var field in rawString.Split('\x01'))
                    {
                        if (string.IsNullOrEmpty(field)) continue;
                        int eqIndex = field.IndexOf('=');
                        if (eqIndex <= 0) continue;
                        if (int.TryParse(field.Substring(0, eqIndex), out int tag))
                        {
                            tags[tag] = field.Substring(eqIndex + 1);
                        }
                    }

                    if (tags.TryGetValue(34, out string seqStr) && int.TryParse(seqStr, out int seqNum))
                    {
                        

                        _expectedInboundSeqNum = seqNum;
                    }

                    ParsedMessageReceived?.Invoke(this, (tags, receiveTimestamp));

                    offset = fullMessageEnd;
                }

                int remaining = bufferLength - offset;
                if (remaining > 0)
                    Buffer.BlockCopy(messageBuffer, offset, messageBuffer, 0, remaining);

                bufferLength = remaining;
            }
        }
        private int FindTag(byte[] data, int start, string tag)
        {
            byte[] tagBytes = Encoding.ASCII.GetBytes(tag);
            for (int i = start; i <= data.Length - tagBytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < tagBytes.Length; j++)
                {
                    if (data[i + j] != tagBytes[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private int findDelimit(byte[] data, int start)
        {
            for (int i = start; i < data.Length; i++)
            {
                if (data[i] == 0x01) return i;
            }
            return -1;
        }

        private bool TryParseAsciiInt(byte[] data, int start, int end, out int result)
        {
            result = 0;
            for (int i = start; i < end; i++)
            {
                if (data[i] < '0' || data[i] > '9') return false;
                result = result * 10 + (data[i] - '0');
            }
            return true;
        }
       
        private int ComputeFixChecksum(string message)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(message);
            int sum = 0;
            foreach (byte b in bytes)
                sum += b;
            return sum % 256;
        }



    }
}
