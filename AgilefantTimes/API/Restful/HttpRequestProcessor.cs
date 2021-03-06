using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AgilefantTimes.API.Agilefant;
using Ionic.Zlib;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace AgilefantTimes.API.Restful
{
    public class HttpRequestProcessor
    {
        private readonly TcpClient _socket;
        private readonly Action<HttpRequestProcessor> _requestHandler;
        private Stream _inputStream;
        private StreamWriter _outputStream;
        public HttpMethod HttpMethod { get; private set; }
        public string HttpUrl { get; private set; }
        public string HttpVersion { get; private set; }
        public Hashtable HttpHeaders { get; private set; }
        public Hashtable HttpResponseHeaders { get; private set; }
        public Hashtable HttpCookies { get; private set; }
        public Hashtable HttpResponseSetCookies { get; private set; }
        public string HttpPostData { get; private set; }
        public string HttpGetData { get; private set; }
        public bool ResponseWritten { get; private set; }

        private const int MaxPostSize = 10485760;
        private const int BufSize = 4096;

        public HttpRequestProcessor(TcpClient tcpClient, Action<HttpRequestProcessor> handleRequest)
        {
            _requestHandler = handleRequest;
            _socket = tcpClient;
            HttpPostData = "";
        }

        public string DecodeAuthenticationHeader()
        {
            var authString = (string) HttpHeaders["Authorization"];
            var data = Convert.FromBase64String(authString.Split(' ')[1]);
            return Encoding.UTF8.GetString(data);
        }

        private string InputReadLine()
        {
            var data = "";
            while (true)
            {
                var nextChar = _inputStream.ReadByte();
                if (nextChar == '\n')
                {
                    break;
                }
                if (nextChar == '\r')
                {
                    continue;
                }
                if (nextChar == -1)
                {
                    Thread.Sleep(1);
                    continue;
                }
                data += Convert.ToChar(nextChar);
            }
            return data;
        }

        public void ProcessInput()
        {
            try
            {
                _inputStream = new BufferedStream(_socket.GetStream());
                _outputStream = new StreamWriter(new BufferedStream(_socket.GetStream()));
            }
            catch (Exception)
            {
                return; // nothing to process
            }

            while (true)
            {
                ResponseWritten = false;
                HttpHeaders = new Hashtable();
                HttpResponseHeaders = new Hashtable();
                HttpCookies = new Hashtable();
                HttpResponseSetCookies = new Hashtable();
                try
                {
                    ParseRequest();
                    ReadHeaders();
                    ReadCookies();

                    Logger.Log("<Thread " + Thread.CurrentThread.ManagedThreadId + "> " + HttpMethod + " " + HttpUrl, LogLevel.Info);
                    switch (HttpMethod)
                    {
                        case HttpMethod.Put:
                        case HttpMethod.Post:
                            GetPostData();
                            _requestHandler.Invoke(this);
                            break;
                        case HttpMethod.Head:
                        case HttpMethod.Delete:
                        case HttpMethod.Get:
                            _requestHandler.Invoke(this);
                            break;
                        case HttpMethod.Options:
                            HttpResponseHeaders["Allow"] = "HEAD,GET,POST,PUT,DELETE,OPTIONS";
                            WriteSuccess();
                            break;
                        default:
                            WriteResponse("405 Method Not Allowed");
                            break;
                    }

                    _outputStream.Flush();
                    _inputStream.Flush();

                    if ((string)HttpHeaders["Connection"] == "close") break;
                }
                catch (Exception e)
                {
                    if (e is SocketException || e is IOException)
                    {
                        // Either Mono died (likely) or someone is using IE/Edge
                        Logger.Log("Connection to client terminated without proper shutdown.", LogLevel.Error);
                        return;
                    }

                    try
                    {
                        // Attempt to tell the user some kind of failure occured.
                        WriteServerFailure();
                        _outputStream.Flush();
                    }
                    catch (Exception)
                    {
                        // Could not tell the user. Ignore the exception because there is not much we can do with it.
                    }
                    
                    Logger.Log(e.StackTrace, LogLevel.Error);
                    break;  // 500 internal server error? probably happened here...
                }
            }

            _inputStream = null;
            _outputStream = null;
            try
            {
                _socket.Close();
            }
            catch (Exception e)
            {
                Logger.Log(e.Message, LogLevel.Error);
            }
        }

        private void ParseRequest()
        {
            var request = InputReadLine();
            var tokens = request.Split(' ');
            if (tokens.Length != 3)
            {
                throw new Exception("invalid http request line");
            }

            switch (tokens[0].ToLower())
            {
                case "get": HttpMethod = HttpMethod.Get; break;
                case "post": HttpMethod = HttpMethod.Post; break;
                case "put": HttpMethod = HttpMethod.Put; break;
                case "head": HttpMethod = HttpMethod.Head; break;
                case "delete": HttpMethod = HttpMethod.Delete; break;
                case "trace": HttpMethod = HttpMethod.Trace; break;
                case "options": HttpMethod = HttpMethod.Options; break;
                default: goto case "trace";
            }

            tokens[1] = Uri.UnescapeDataString(tokens[1]);
            var index = tokens[1].IndexOf('?');
            if (index >= 0)
            {
                var getParams = tokens[1].Substring(index);
                tokens[1] = tokens[1].Substring(0, index);
                if (getParams.Length > 1)
                {
                    HttpGetData = getParams.Substring(1);
                }
            }

            HttpUrl = tokens[1];
            HttpVersion = tokens[2];
        }

        private void ReadCookies()
        {
            var cookie = (string)HttpHeaders["Cookie"];
            if (cookie == null) return;
            var cookies = cookie.Split(new []{"; ", ";"}, StringSplitOptions.RemoveEmptyEntries);
            foreach (var spl in cookies.Select(s => s.Split('=')))
            {
                HttpCookies[spl[0].Trim()] = spl[1].Trim();
            }
        }

        public Dictionary<string, string> ParseGetParameters()
        {
            var items = HttpGetData.Split('&');
            var dict = new Dictionary<string, string>();
            foreach (var item in items)
            {
                var name = Uri.UnescapeDataString(item.Substring(0, item.IndexOf('=')));
                var val = Uri.UnescapeDataString(item.Substring(name.Length + 1));
                dict[name] = val;
            }
            return dict;
        }

        private void ReadHeaders()
        {
            string line;
            while ((line = InputReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                var separator = line.IndexOf(':');
                if (separator == -1)
                {
                    throw new Exception("Invalid HTTP header: \"" + line + "\"");
                }
                var name = line.Substring(0, separator);
                var value = line.Substring(separator + 1).Trim();
                HttpHeaders[name] = value;
            }
        }

        private void GetPostData()
        {
            var ms = new MemoryStream();
            if (HttpHeaders.ContainsKey("Content-Length"))
            {
                var contentLen = Convert.ToInt32(HttpHeaders["Content-Length"]);
                if (contentLen > MaxPostSize)
                {
                    throw new Exception($"POST Content-Length({contentLen}) too big!");
                }
                var buf = new byte[BufSize];
                var toRead = contentLen;
                while (toRead > 0)
                {
                    var numread = _inputStream.Read(buf, 0, Math.Min(BufSize, toRead));
                    if (numread == 0)
                    {
                        if (toRead == 0)
                        {
                            break;
                        }
                        throw new Exception("Client disconnected while reading POST data.");
                    }
                    toRead -= numread;
                    ms.Write(buf, 0, numread);
                }
                ms.Seek(0, SeekOrigin.Begin);
            }

            var reader = new StreamReader(ms);
            HttpPostData = reader.ReadToEnd();
            reader.Close();
        }

        public void WriteSuccess(string response = null, string contentType = "application/json")
        {
            WriteResponse("200 OK", response, contentType);
        }

        public void WriteAuthRequired(bool basicAuthentication = true, string loginMessage = "Login Required",
            string errorMessage = "<b>401, Thou must login before slaying dragons.</b>", string contentType = null)
        {
            if (basicAuthentication)
            {
                HttpResponseHeaders["WWW-Authenticate"] = "Basic realm=\"" + loginMessage + "\"";
            }
            WriteResponse("401 Not Authorized", errorMessage, contentType);
        }

        public void WriteRedirect(string location, string data = "{\"success\":true}", string contentType = "application/json")
        {
            HttpResponseHeaders["Location"] = location;
            WriteResponse("302 Found", data, contentType);
        }

        private void WriteServerFailure(string errorMessage = "<b>500, Oh fiddlesticks! That's an error and it is all YOUR fault.</b>")
        {
            HttpHeaders["Connection"] = "close";
            WriteResponse("500 Internal Server Error", errorMessage);
        }

        private static byte[] GzipCompressString(string str)
        {
            byte[] result;
            var bytes = Encoding.UTF8.GetBytes(str);
            using (var st = new MemoryStream())
            {
                using (var gzs = new GZipStream(st, CompressionMode.Compress, true))
                {
                    gzs.Write(bytes, 0, bytes.Length);
                }
                st.Position = 0;
                result = new byte[st.Length];
                st.Read(result, 0, result.Length);
            }
            return result;
        }

        public void WriteResponse(string status, string response = null, string contentType = null)
        {
            if (ResponseWritten) throw new Exception("Cannot send new response after response has been sent.");
            ResponseWritten = true;

            Logger.Log("<Thread " + Thread.CurrentThread.ManagedThreadId + "> Response: " + status, status.StartsWith("2") ? LogLevel.Success : LogLevel.Warn);
            byte[] encodedBytes = null;
            var length = string.IsNullOrWhiteSpace(response) ? 0 : Encoding.UTF8.GetByteCount(response);
            if (HttpHeaders.ContainsKey("Accept-Encoding") && ((string)HttpHeaders["Accept-Encoding"]).Contains("gzip") && response != null)
            {
                HttpResponseHeaders["Content-Encoding"] = "gzip";
                encodedBytes = GzipCompressString(response);
                contentType += "; charset=utf-8";
                length = encodedBytes.Length;
            }

            HttpResponseHeaders["X-Powered-By"] = "Knoxius Servius";
            HttpResponseHeaders["Access-Control-Allow-Origin"] = "*";

            _outputStream.WriteLine("HTTP/1.1 " + status);
            var connection = (string) HttpHeaders["Connection"];
            if (string.IsNullOrWhiteSpace(connection))
            {
                connection = "close";
                HttpHeaders["Connection"] = connection;
            }
            _outputStream.WriteLine("Connection: " + connection);
            if (!string.IsNullOrWhiteSpace(response))
            {
                contentType = string.IsNullOrWhiteSpace(contentType) ? "text/html" : contentType;
                _outputStream.WriteLine("Content-Type: " + contentType);
                _outputStream.WriteLine("Content-Length: " + length);
            }
            foreach (var header in HttpResponseHeaders.Keys)
            {
                _outputStream.WriteLine(header + ": " + HttpResponseHeaders[header]);
            }
            foreach (var cookie in HttpResponseSetCookies.Keys)
            {
                _outputStream.WriteLine("Set-Cookie: " + cookie + "=" + HttpResponseSetCookies[cookie]);
            }
            _outputStream.WriteLine("");
            if (string.IsNullOrWhiteSpace(response) || HttpMethod == HttpMethod.Head) return;
            if (encodedBytes != null)
            {
                _outputStream.Flush();
                _outputStream.BaseStream.Write(encodedBytes, 0, encodedBytes.Length);
            }
            else
            {
                _outputStream.Write(response);
            }
        }
    }
}