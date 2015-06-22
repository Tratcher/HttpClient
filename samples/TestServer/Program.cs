using System;
using System.Net;
using System.Text;

namespace TestServer
{
    public class Program
    {
        public void Main(string[] args)
        {
            var server = new HttpListener();
            server.Prefixes.Add("http://localhost:8080/");
            Console.WriteLine("Listening on http://localhost:8080/");
            server.Start();

            while (true)
            {
                var requestContext = server.GetContext();
                Console.WriteLine($"Received: {requestContext.Request.HttpMethod} {requestContext.Request.Url} HTTP/{requestContext.Request.ProtocolVersion.ToString(2)}");

                var message = "Hello World " + DateTime.UtcNow;
                var bytes = Encoding.UTF8.GetBytes(message);
                requestContext.Response.OutputStream.Write(bytes, 0, bytes.Length);
                requestContext.Response.Close();
            }
        }
    }
}
