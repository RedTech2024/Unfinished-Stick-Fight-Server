using System;
using System.Threading;

namespace StickFightLanServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Starting Stick Fight LAN Server...");
            GameServer server = new GameServer();
            
            int port = 1337; 
            if (args.Length > 0 && int.TryParse(args[0], out int customPort))
            {
                port = customPort;
            }

            server.Start(port);

            Console.WriteLine("Server is running. Press Ctrl+C to stop.");

           
            var exitEvent = new ManualResetEvent(false);
            Console.CancelKeyPress += (sender, eventArgs) => 
            {
                eventArgs.Cancel = true; 
                exitEvent.Set();      
            };
            
            exitEvent.WaitOne(); 

            Console.WriteLine("Shutdown signal received.");
            server.Stop();
            Console.WriteLine("Application finished.");
        }
    }
} 