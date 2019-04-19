using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using RemoteViewing.Utility;
using RemoteViewing.Vnc;
using RemoteViewing.Vnc.Server;
using RemoteViewing.Windows.Forms.Server;

namespace RemoteViewing.VncPikeServer
{
    internal class Program
    {
        private static string password = "test";
        private static VncServerSession session;

        private static void HandleConnected(object sender, EventArgs e)
        {
            Console.WriteLine("Connected");
        }

        private static void HandleConnectionFailed(object sender, EventArgs e)
        {
            Console.WriteLine("Connection Failed");
        }

        private static void HandleClosed(object sender, EventArgs e)
        {
            Console.WriteLine("Closed");
        }

        private static void HandlePasswordProvided(object sender, PasswordProvidedEventArgs e)
        {
            e.Accept(password.ToCharArray());
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 6 || args.Length == 10)
            {
                string serverId = args[0];
                string serverName = args[1];
                IPAddress repeaterAddr = IPAddress.Parse(args[2]);
                int repeaterPort = int.Parse(args[3]);
                string passwordViewer = args[4];
                string passwordLooker = args[5];
                System.Drawing.Rectangle bounds = System.Drawing.Rectangle.Empty;

                Console.WriteLine($@"Opposite server starting...");
                Console.WriteLine($@"Server ID: {serverId}");
                Console.WriteLine($@"Server ID: {serverName}");
                Console.WriteLine($@"Repeater: {repeaterAddr}:{repeaterPort}");
                Console.WriteLine($@"Password viewer: {passwordViewer}");
                Console.WriteLine($@"Password looker: {passwordLooker}");

                if (args.Length == 10)
                {
                    bounds = new System.Drawing.Rectangle
                    {
                        X = int.Parse(args[6]),
                        Y = int.Parse(args[7]),
                        Width = int.Parse(args[8]),
                        Height = int.Parse(args[9]),
                    };

                    Console.WriteLine($@"Bounds: {bounds}");
                }

                OppositeServer os = new OppositeServer(serverId, serverName, repeaterAddr, repeaterPort, passwordViewer, passwordLooker, bounds);
                os.Start();
            }

            // Это прямой сервер, для тестов, его можно выключить

            // Wait for a connection.
            var listener = new TcpListener(IPAddress.Any, 5905);
            listener.Start();

            float scale = 0.7f;

            while (true)
            {
                var client = listener.AcceptTcpClient();

                // Console.WriteLine("Client connected");

                // Set up a framebuffer and options.
                var options = new VncServerSessionOptions();
                options.AuthenticationMethod = AuthenticationMethod.Password;

                // Virtual mouse
                var mouse = new VncMouse(scale);

                // Virtual keyboard
                var keyboard = new VncKeyboard();

                // Create a session.
                session = new VncServerSession();

                session.PasswordLooker = "test1";
                session.Connected += HandleConnected;
                session.ConnectionFailed += HandleConnectionFailed;
                session.Closed += HandleClosed;
                session.PasswordProvided += HandlePasswordProvided;
                session.SetFramebufferSource(new VncScreenFramebufferSource("Hey dude!", Screen.PrimaryScreen, scale));
                session.PointerChanged += mouse.OnMouseUpdate;
                session.KeyChanged += keyboard.OnKeyboardUpdate;
                session.Connect(client.GetStream(), options);
            }
        }
    }
}
