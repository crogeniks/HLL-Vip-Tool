using CommandLine;
using CsvHelper;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace hll_vips_tool
{
    public class Program
    {
        public const string VipFile = "vips.csv";
        public const string ServerFile = "servers.json";

        static async Task Main(string[] args)
        {
            await Parser
                .Default
                .ParseArguments<Options>(args)
                .WithParsedAsync(async o =>
                {
                    if (o.Mode == ToolMode.Tool)
                    {
                        await RunToolMode();
                    }
                    else
                    {
                        while (true)
                        {
                            await RunServerMode();

                            await Task.Delay(TimeSpan.FromMinutes(o.Delay));
                        }
                    }
                });

            Console.ForegroundColor = ConsoleColor.White;
        }

        private static async Task RunToolMode()
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Welcome to HLL VIP tool");
            List<Server> servers = new List<Server>();

            if (File.Exists(ServerFile))
            {
                servers.AddRange(JsonConvert.DeserializeObject<List<Server>>(File.ReadAllText(ServerFile)));
            }

            bool run = true;
            do
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Menu :");
                Console.WriteLine("1 - Add a server");
                Console.WriteLine("2 - Export and merge vips");
                Console.WriteLine("3 - Import");
                Console.WriteLine("4 - exit");

                Console.WriteLine("--------");
                Console.WriteLine("5 - List Servers");
                Console.WriteLine("6 - Delete Server");

                var choice = Console.ReadLine();

                switch (choice)
                {
                    default:
                        break;
                    case "1":
                        await AddServer(servers);
                        break;
                    case "2":
                        await ExportVips(servers);
                        break;
                    case "3":
                        await ImportVips(servers);
                        break;
                    case "4":
                        run = false;
                        break;
                    case "5":
                        ListServers(servers);
                        break;
                    case "6":
                        await DeleteServer(servers);
                        break;
                }
            }
            while (run);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Done. Press any key to close.");
            Console.ReadKey();
        }

        private static async Task RunServerMode()
        {
            List<Server> servers = new List<Server>();

            if (!File.Exists(VipFile))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"No {VipFile} present. Cannot run server mode.");
                return;
            }

            if (File.Exists(ServerFile))
            {
                servers.AddRange(JsonConvert.DeserializeObject<List<Server>>(File.ReadAllText(ServerFile)));
            }
            if (!servers.Any())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("No servers configured. Cannot run server mode.");
                return;
            }

            await InnerImport(servers);
        }

        private static async Task AddServer(List<Server> servers)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Enter HLL Server IP :");
            var ip = Console.ReadLine();

            Console.WriteLine("Enter HLL Server RCON Port :");
            var port = int.Parse(Console.ReadLine());

            Console.WriteLine("Enter HLL Server RCON Password:");
            var password = Console.ReadLine();

            servers.Add(new Server
            {
                Id = Guid.NewGuid().ToString(),
                Ip = ip,
                Port = port,
                Password = password
            });

            if (File.Exists(ServerFile))
            {
                File.Delete(ServerFile);
            }
            var serversJson = JsonConvert.SerializeObject(servers);
            await File.WriteAllTextAsync(ServerFile, serversJson);
        }

        private static async Task ExportVips(List<Server> servers)
        {
            List<string> vips = new List<string>();

            foreach (var server in servers)
            {
                try
                {
                    using var client = new TcpClient();

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Connecting to {server.Ip}:{server.Port}");
                    await client.ConnectAsync(server.Ip, server.Port);

                    var xor = await ReceiveBytes(client, null, false);

                    if (xor.Length == 0)
                    {
                        throw new Exception("Invalid XOR");
                    }

                    SendMessage(client, xor, string.Format("login {0}", server.Password), true);
                    var response = await ReceiveMessage(client, xor, true);
                    if (response != null && response.Equals("SUCCESS"))
                    {
                        Console.WriteLine($"Connected to {server.Ip}:{server.Port}");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Disconnected, wrong password?");
                        continue;
                    }

                    SendMessage(client, xor, "get vipids", true);

                    var vipsRaw = await ReceiveMessage(client, xor, true);

                    vips.AddRange(vipsRaw.Split('\t', StringSplitOptions.RemoveEmptyEntries).Skip(1));

                    Console.WriteLine($"VIP Exported.");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error : {ex.Message} {ex.StackTrace}");
                }
            }

            if (vips.Any())
            {
                if (File.Exists(VipFile))
                {
                    File.Delete(VipFile);
                }

                var vipRecords = vips
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(raw =>
                    {
                        var parts = raw.Split(' ', 2);
                        return new VipRecord
                        {
                            Name = parts[1].Replace("\"", ""),
                            SteamId = parts[0],
                        };
                    });

                using (var writer = new StreamWriter(VipFile))
                using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
                {
                    csv.WriteRecords<VipRecord>(vipRecords);
                }
            }
        }

        private static async Task ImportVips(List<Server> servers)
        {
            if (!File.Exists(VipFile))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"vip file doesn't exists");
                return;
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Do you want to delete all and import or only add vips (y/n)? (y = delete all, make sure ALL desired vips are in {VipFile} file");

            var answer = Console.ReadLine();
            await InnerImport(servers, answer);
        }

        private static async Task InnerImport(List<Server> servers, string answer = "y")
        {
            List<VipRecord> vips = new List<VipRecord>();

            using (var reader = new StreamReader(VipFile))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                vips = csv.GetRecords<VipRecord>().ToList();
            }


            foreach (var server in servers)
            {
                using var client = new TcpClient();

                await client.ConnectAsync(server.Ip, server.Port);

                var xor = await ReceiveBytes(client, null, false);

                if (xor.Length == 0)
                {
                    throw new Exception("Invalid XOR");
                }

                SendMessage(client, xor, string.Format("login {0}", server.Password), true);
                var response = await ReceiveMessage(client, xor, true);
                if (response != null && response.Equals("SUCCESS"))
                {
                    Console.WriteLine($"Connected to {server.Ip}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Disconnected, wrong password?");
                    continue;
                }

                SendMessage(client, xor, "get vipids", true);

                var vipsRaw = await ReceiveMessage(client, xor, true);

                var serverVips = vipsRaw.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (answer.ToLowerInvariant() == "y")
                {
                    foreach (var vip in serverVips.Skip(1))
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine($"Deleting VIP " + vip);
                        SendMessage(client, xor, $"vipdel {vip.Split(' ')[0]}");
                        await ReceiveMessage(client, xor, true);
                    }
                    serverVips = new string[0];
                }

                Console.ForegroundColor = ConsoleColor.White;
                foreach (var vip in vips)
                {
                    if (serverVips.Any(s => s.Contains(vip.SteamId)))
                    {
                        Console.WriteLine($"skipping VIP " + vip.Name);
                        continue;
                    }

                    Console.WriteLine($"Adding VIP " + vip.Name);
                    SendMessage(client, xor, $"vipadd {vip.SteamId} \"{vip.Name.Replace("\"", "").Trim()}\"");
                    await ReceiveMessage(client, xor, true);
                }

            }
        }

        private static void ListServers(List<Server> servers)
        {
            Console.WriteLine("");
            Console.WriteLine("");
            Console.WriteLine("--------------------");

            foreach (var server in servers)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"{server.Ip}:{server.Port} - {server.Password} | {server.Id}");
            }
            Console.WriteLine("--------------------");
            Console.WriteLine("");
            Console.WriteLine("");
        }

        private static async Task DeleteServer(List<Server> servers)
        {
            ListServers(servers);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Enter the ID of the server to delete (Looks like {servers.FirstOrDefault()?.Id})");

            var id = Console.ReadLine();

            var toDelete = servers.FirstOrDefault(s => s.Id == id);

            if (toDelete is null)
            {

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ID doesn't match any server");
                return;
            }

            servers.Remove(toDelete);

            if (File.Exists(ServerFile))
            {
                File.Delete(ServerFile);
            }

            var serversJson = JsonConvert.SerializeObject(servers);
            await File.WriteAllTextAsync(ServerFile, serversJson);
        }

        //Communication helpers
        private static bool SendMessage(TcpClient client, byte[] xor, string message, bool encrypted = true)
        {
            return SendBytes(client, xor, Encoding.UTF8.GetBytes(message), encrypted);
        }

        private static bool SendBytes(TcpClient client, byte[] xor, byte[] message, bool encrypted = true)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                if (encrypted)
                {
                    message = XORMessage(xor, message);
                }
                byte[] buffer = message;
                int length = message.Length;
                stream.Write(buffer, 0, length);
                return true;
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        private static async Task<string> ReceiveMessage(TcpClient client, byte[] xor, bool decrypted = true, bool crash = false)
        {
            string receivedMessage = "";
            byte[] receivedBytes;

            do
            {
                receivedBytes = await ReceiveBytes(client, xor, decrypted, crash);
                if (receivedBytes == null)
                {
                    return null;
                }

                try
                {
                    receivedMessage += Encoding.UTF8.GetString(receivedBytes, 0, receivedBytes.Length);

                    await Task.Delay(400); //small delay to let the server answer
                }
                catch (DecoderFallbackException ex)
                {
                    throw;
                }
            }
            while (client.GetStream().DataAvailable);

            return receivedMessage;
        }

        private static async Task<byte[]> ReceiveBytes(TcpClient client, byte[] xor, bool decrypted = true, bool crash = false)
        {
            var receivedBytes = new byte[8196];
            int newSize;

            var cts = new CancellationTokenSource(6000);

            try
            {
                if (crash) throw new Exception();
                newSize = await client.GetStream().ReadAsync(receivedBytes, 0, receivedBytes.Length, cts.Token);
            }
            catch (Exception ex)
            {
                // Trace.TraceError($"{ID} : ReceiveBytes ERROR {ex.Message}; {ex.StackTrace}");
                throw;
            }
            finally
            {
                cts.Dispose();
            }

            Array.Resize<byte>(ref receivedBytes, newSize);
            if (decrypted)
            {
                receivedBytes = XORMessage(xor, receivedBytes);
            }

            return receivedBytes;
        }

        private static byte[] XORMessage(byte[] xor, byte[] message)
        {
            try
            {
                for (int index = 0; index < message.Length; ++index)
                {
                    message[index] ^= xor[index % xor.Length];
                }
                return message;
            }
            catch (Exception ex)
            {
                return new byte[0];
            }
        }

    }

    public class Options
    {
        [Option('m', "mode", Required = false, HelpText = "Default is Tool : Runs once. Use Server mode to periodically run the tool on a set of servers", Default = ToolMode.Tool)]
        public ToolMode Mode { get; set; }

        [Option('d', "delay", Required = false, Default = 15, HelpText = "Dealy in minutes between each VIP sync")]
        public int Delay { get; set; }
    }

    public enum ToolMode
    {
        Tool,
        Server
    }

    public class VipRecord
    {
        public string Name { get; set; }
        public string SteamId { get; set; }
    }

    class Server
    {
        public string Id { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public string Password { get; set; }
    }
}
