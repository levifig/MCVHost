using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MCVHost
{
    class Program
    {
        // Variables to be used for the duration of the program's execution

        // The port for the proxy to listen to
        static int listnerPort = 25565;
        // The endpoint to listen on.  0.0.0.0 is all available interfaces.
        static string listenerEndPoint = "0.0.0.0";
        // A dictionary of hosts and destinations for each virtual host
        static Dictionary<string, string> VirtualHosts = new Dictionary<string, string>();
        // The default host to use if there is no match
        static string defaultHost = null;
        // This is what we'll use to listen for incoming traffic
        static TcpListener listener;
        // We can't determine which host to use from an 0xFE request, so we have to specify a custom, global message of the day
        static string MotD = "A Minecraft Proxy";
        // If true, fetch ping values from all remote servers
        static bool FetchPingEnabled = false;

        static void Main(string[] args)
        {
            // Open the config file
            StreamReader reader = new StreamReader(File.Open("config.xml", FileMode.Open));
            // Read it into an xdocument object
            // XDocuments can be used to traverse XML
            XDocument document = XDocument.Parse(reader.ReadToEnd());
            // Close the stream
            reader.Close();

            LoadConfig(document);

            // Open up a listener on the specified endpoint
            listener = new TcpListener(IPAddress.Parse(listenerEndPoint), listnerPort);

            // Start listening for traffic
            listener.Start();
            listener.BeginAcceptTcpClient(HandleClient, null);

            Console.WriteLine("Listening to " + listenerEndPoint + ":" + listnerPort.ToString());

            while (Console.ReadLine() != "quit") ; // Exit when "quit" has been typed into the console.

            listener.Stop();
        }

        static void HandleClient(IAsyncResult result)
        {
            // Listen for the next connection
            listener.BeginAcceptTcpClient(HandleClient, null);

            // Retrieve the new connection
            TcpClient remoteClient = listener.EndAcceptTcpClient(result);

            // Handle the new connection
            Thread t = new Thread(new ParameterizedThreadStart(HandleClient));
            t.Start(remoteClient);
        }

        private static void HandleClient(object remoteClientObj)
        {
            TcpClient remoteClient = (TcpClient)remoteClientObj;
            // Listen for a handshake packet (or a server list ping)

            // This method is executed asyncronously, and is not blocking,
            // so this code can exist with feeling bad about it.
            // Seriously, though, don't actually use this kind of packet
            // interpreter/parser/whatever.  I'm only handling two packets
            // here.

            string host = defaultHost;
            string originalHost = null;
            string username = null;

            try
            {
                byte b = (byte)remoteClient.GetStream().ReadByte();

                if (b == 0xFE) // Server list ping
                {
                    byte[] payload = new byte[0];
                    if (FetchPingEnabled)
                    {
                        // This will count all the players on all the vhosts
                        int players = 0;
                        int max = 0;
                        try
                        {
                            foreach (String s in VirtualHosts.Values)
                            {
                                TcpClient remoteServer = new TcpClient();
                                if (s.Contains(":"))
                                {
                                    string[] parts = s.Split(':');
                                    remoteServer.ReceiveTimeout = 10000; // Ten second timeout
                                    remoteServer.Connect(parts[0], int.Parse(parts[1]));
                                }
                                else
                                {
                                    remoteServer.ReceiveTimeout = 10000; // Ten second timeout
                                    remoteServer.Connect(s, 25565);
                                }
                                byte[] handshakePacket = new byte[] { 0xFE }.ToArray();
                                remoteServer.GetStream().Write(handshakePacket, 0, handshakePacket.Length);
                                bool hasData = false;
                                while (!hasData)
                                {
                                    if (remoteServer.Available != 0)
                                    {
                                        // Read any waiting data
                                        const char space = '\u0000';
                                        byte[] buffer = new byte[remoteServer.Available];
                                        remoteServer.GetStream().Read(buffer, 0, buffer.Length);
                                        String[] motd = System.Text.Encoding.ASCII.GetString(buffer, 0, buffer.Length).Split('\u003f');
                                        players = players + Convert.ToInt32(new System.Text.RegularExpressions.Regex(space.ToString()).Replace(motd[2], string.Empty));
                                        max = max + Convert.ToInt32(new System.Text.RegularExpressions.Regex(space.ToString()).Replace(motd[3], string.Empty));
                                        remoteServer.Close();
                                        hasData = true;
                                    }
                                    Thread.Sleep(5);
                                }
                            }
                            payload = new byte[] { 0xFF }.Concat(MakeString(MotD + "§" + players.ToString() + "§" + max.ToString())).ToArray(); // Construct a packet to respond with
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message + ex.StackTrace);
                        }
                    }
                    else
                        payload = new byte[] { 0xFF }.Concat(MakeString(MotD)).ToArray();

                    remoteClient.GetStream().Write(payload, 0, payload.Length);
                    remoteClient.Close();
                    return;
                }
                else if (b == 0x02) // Handshake
                {
                    // Retrieve the username and hostname, and parse it
                    string userAndHost = ReadString(remoteClient.GetStream());
                    string[] parts = userAndHost.Split(';');

                    if (parts.Length == 2)
                        host = parts[1];
                    if (!host.Contains(':'))
                        host += ":25565";

                    originalHost = host;

                    if (VirtualHosts.ContainsKey(host.ToLower()))
                        host = VirtualHosts[host.ToLower()];
                    else
                        host = defaultHost;

                    username = parts[0];

                    // At this point, username should be the username, and host should be the destination host to connect to

                    if (host == null)
                    {
                        // If there is no host, then disconnect the user.
                        if (parts.Length == 2)
                            Console.WriteLine(username + " tried to log in to " + parts[1] + ", and cannot be redirected.");
                        else
                            Console.WriteLine(username + " tried to log in and cannot be redirected.");
                        // Disconnect user
                        byte[] payload = new byte[] { 0xFF }.Concat(MakeString("[Proxy Error]: Unable to redirect to destination.")).ToArray();
                        remoteClient.GetStream().Write(payload, 0, payload.Length);
                        remoteClient.Close();
                        return;
                    }
                }
                else // Something unexpected
                {
                    remoteClient.Close();
                    return;
                }
            }
            catch { }

            // If we got this far, we should be able to connect the user properly.
            try
            {
                Console.WriteLine(username + " logged in to " + originalHost + ", redirecting to " + host);

                // Connect to the requested server
                TcpClient remoteServer = new TcpClient();
                string[] parts = host.Split(':');
                remoteServer.ReceiveTimeout = 10000; // Ten second timeout
                remoteServer.Connect(parts[0], int.Parse(parts[1]));

                // Create and send a handshake packet
                byte[] handshakePacket = new byte[] { 0x02 }.Concat(MakeString(username + ";" + host)).ToArray();
                remoteServer.GetStream().Write(handshakePacket, 0, handshakePacket.Length);

                // Get the two talking
                while (remoteServer.Connected && remoteClient.Connected)
                {
                    // Read from the server
                    if (remoteServer.Available != 0)
                    {
                        // Read any waiting data
                        byte[] buffer = new byte[remoteServer.Available];
                        remoteServer.GetStream().Read(buffer, 0, buffer.Length);
                        // And write it back to the client.
                        remoteClient.GetStream().Write(buffer, 0, buffer.Length);
                    }
                    // Read from the client
                    if (remoteClient.Available != 0)
                    {
                        // Read any waiting data
                        byte[] buffer = new byte[remoteClient.Available];
                        remoteClient.GetStream().Read(buffer, 0, buffer.Length);
                        // And write it back to the server.
                        remoteServer.GetStream().Write(buffer, 0, buffer.Length);
                    }
                    Thread.Sleep(1); // If you don't do this, you will ruin your processor
                }
            }
            catch
            {
                try
                {
                    // Disconnect the user.
                    byte[] payload = new byte[] { 0xFF }.Concat(MakeString("[Proxy Error]: Unable to connect to remote server.")).ToArray();
                    remoteClient.GetStream().Write(payload, 0, payload.Length);
                    remoteClient.Close();
                }
                catch { }
            }
        }

        private static void LoadConfig(XDocument document)
        {
            // Parse the config
            if (document.Root.Element("port") != null)
                listnerPort = int.Parse(document.Root.Element("port").Value);
            if (document.Root.Element("endpoint") != null)
                listenerEndPoint = document.Root.Element("endpoint").Value;
            if (document.Root.Element("motd") != null)
                MotD = document.Root.Element("motd").Value;
            if (document.Root.Element("pingremote") != null)
                FetchPingEnabled = bool.Parse(document.Root.Element("pingremote").Value);

            if (document.Root.Element("vhosts") == null)
            {
                // We should only attempt to execute if there are defined virutal hosts
                Console.WriteLine("[Error]: No virtual hosts defined in config.xml!");
                return;
            }

            // Iterate through each defined virtual host
            foreach (XElement element in document.Root.Element("vhosts").Elements("vhost"))
            {
                // Validate the XML element
                if (element.Attribute("host") == null || element.Attribute("destination") == null)
                {
                    Console.WriteLine("[Error]: Config file has invalid hosts");
                    return;
                }

                // Parse this element into a virtual host
                string host = element.Attribute("host").Value;
                // Ensure it has a port
                if (!host.Contains(':'))
                    host += ":25565";
                VirtualHosts.Add(host, element.Attribute("destination").Value);
            }

            // Parse the default host from XML
            if (document.Root.Element("vhosts").Element("default") == null)
                Console.WriteLine("[Warning]: No default host specified.");
            else
            {
                XElement defaultVHost = document.Root.Element("vhosts").Element("default");
                // Validate it
                if (defaultVHost.Attribute("destination") == null)
                {
                    Console.WriteLine("[Error]: Default host is invalid");
                    return;
                }

                // Parse it
                defaultHost = defaultVHost.Attribute("destination").Value;
                if (!defaultHost.Contains(':'))
                    defaultHost += ":25565";
            }
        }

        // Code stolen from my other projects follows.
        public static byte[] MakeString(String msg)
        {
            short len = IPAddress.HostToNetworkOrder((short)msg.Length);
            byte[] a = BitConverter.GetBytes(len);
            byte[] b = Encoding.BigEndianUnicode.GetBytes(msg);
            return a.Concat(b).ToArray();
        }

        public static string ReadString(Stream s)
        {
            byte[] lengthArray = new byte[sizeof(short)];
            s.Read(lengthArray, 0, sizeof(short));

            short length = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(lengthArray, 0));

            byte[] stringArray = new byte[length * 2];
            s.Read(stringArray, 0, length * 2);

            return Encoding.BigEndianUnicode.GetString(stringArray);
        }
    }
}
