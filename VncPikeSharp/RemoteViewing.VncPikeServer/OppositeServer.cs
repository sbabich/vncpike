using RemoteViewing.Windows.Forms.Server;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteViewing.Vnc.Server
{
    /// <summary>
    /// Обратный VNC Server
    /// </summary>
    public class OppositeServer : IDisposable
    {
        /// <summary>
        /// Gets ServerID.
        /// </summary>
        public string ServerId { get; private set; }

        /// <summary>
        /// Gets Server name.
        /// </summary>
        public string ServerName { get; private set; }

        /// <summary>
        /// Gets Repeater IP Address
        /// </summary>
        public IPAddress RepeaterAddr { get; private set; }

        /// <summary>
        /// Gets Repeater Port.
        /// </summary>
        public int RepeaterPort { get; private set; }

        /// <summary>
        /// Gets Repeater Port.
        /// </summary>
        public string PasswordViewer { get; private set; }

        /// <summary>
        /// Gets Repeater Port.
        /// </summary>
        public string PasswordLooker { get; private set; }

        /// <summary>
        /// Gets Repeater Port.
        /// </summary>
        public System.Drawing.Rectangle ScreenBounds { get; private set; }

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// Initializes a new instance of the <see cref="OppositeServer"/> class.
        /// Конструктор обратного сервер
        /// </summary>
        /// <param name="serverId">Иднетификатор сервера аля ТВ</param>
        /// <param name="serverName">Имя сервера, любая строка</param>
        /// <param name="repeaterAddr">Адрес репитера</param>
        /// <param name="repeaterPort">Порт репитера</param>
        /// <param name="passwordViewer">Пароль для подкючения вьюера</param>
        /// <param name="passwordLooker">Пароль для подключения зрителя</param>
        /// <param name="screenBounds">Границы экрана</param>
        public OppositeServer(
            string serverId, 
            string serverName, 
            IPAddress repeaterAddr, 
            int repeaterPort, 
            string passwordViewer, 
            string passwordLooker,
            System.Drawing.Rectangle screenBounds)
        {
            this.ServerId = serverId;
            this.ServerName = serverName;
            this.RepeaterAddr = repeaterAddr;
            this.RepeaterPort = repeaterPort;
            this.PasswordViewer = passwordViewer;
            this.PasswordLooker = passwordLooker;
            this.ScreenBounds = screenBounds;
        }

        /// <summary>
        /// Start Opposite server.
        /// </summary>
        public void Start()
        {
            CancellationToken token = this._cts.Token;
            float scale = 0.7f;

            Thread t = new Thread(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
                        {
                            Console.WriteLine($"Try to connect at {this.RepeaterAddr}:{this.RepeaterPort} SERVERID={this.ServerId}");
                            await client.ConnectAsync(this.RepeaterAddr, this.RepeaterPort);

                            using (NetworkStream ns = client.GetStream())
                            {
                                byte[] buff = Encoding.UTF8.GetBytes(this.ServerId);
                                await ns.WriteAsync(buff, 0, buff.Length, token);

                                Console.WriteLine($"Connected, send ID={this.ServerId}");

                                // Ждем привета от клиента

                                // Set up a framebuffer and options.
                                var options = new VncServerSessionOptions();
                                options.AuthenticationMethod = AuthenticationMethod.Password;

                                // Virtual mouse
                                var mouse = new VncMouse(scale);

                                // Virtual keyboard
                                var keyboard = new VncKeyboard();

                                bool is_alive = true;

                                // Create a session.
                                var session = new VncServerSession();
                                session.PasswordLooker = this.PasswordLooker;

                                session.ConnectionFailed += (object sender, EventArgs e) =>
                                {
                                    is_alive = false;
                                    Console.WriteLine("Connection Failed");
                                };

                                session.Closed += (object sender, EventArgs e) =>
                                {
                                    is_alive = false;
                                    Console.WriteLine("Closed");
                                };

                                session.Connected += this.HandleConnected;
                                session.PasswordProvided += this.HandlePasswordProvided;

                                // full screen
                                if (this.ScreenBounds.IsEmpty)
                                {
                                    session.SetFramebufferSource(new VncScreenFramebufferSource(this.ServerName, Screen.PrimaryScreen, scale));
                                }
                                else
                                {
                                    session.SetFramebufferSource(new VncScreenFramebufferSource(this.ServerName, () => { return this.ScreenBounds; }, scale));
                                }

                                session.PointerChanged += mouse.OnMouseUpdate;
                                session.KeyChanged += keyboard.OnKeyboardUpdate;

                                session.Connect(ns, options);

                                while (is_alive && !token.IsCancellationRequested)
                                {
                                    await Task.Delay(500, token);
                                }
                            }
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        await Task.Delay(2000, token);
                    }
                    finally
                    {
                    }
                }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
        }

        private void HandleConnected(object sender, EventArgs e)
        {
            Console.WriteLine("Connected");
        }

        private void HandlePasswordProvided(object sender, PasswordProvidedEventArgs e)
        {
            string passw = $@"{this.PasswordViewer}";
            e.Accept(passw.ToCharArray());
        }

        /// <summary>
        /// Stop the opposite server.
        /// </summary>
        public void Stop()
        {
            this._cts.Cancel();
        }

        /// <summary>
        /// Dispose server.
        /// </summary>
        public void Dispose()
        {
            this.Stop();
        }
    }
}
