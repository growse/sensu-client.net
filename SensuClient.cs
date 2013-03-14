using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Framing.v0_9_1;

namespace sensu_client.net
{
    class SensuClient : ServiceBase
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const int KeepAliveTimeout = 20000;
        private static readonly object MonitorObject = new object();
        private static bool _quitloop;
        private static JObject _configsettings;
        private const string Configfile = "config.json";
        private const string Configdir = "conf.d";
        public static void Start()
        {

            //Read Config settings
            try
            {
                _configsettings = JObject.Parse(File.ReadAllText(Configfile));
            }
            catch (FileNotFoundException ex)
            {
                Log.ErrorException(string.Format("Config file not found: {0}", Configfile), ex);
            }
            
            //Grab configs from dir.
            foreach (var settings in Directory.EnumerateFiles(Configdir).Select(file => JObject.Parse(File.ReadAllText(file))))
            {
                foreach (var thingemebob in settings)
                {
                    _configsettings.Add(thingemebob.Key, thingemebob.Value);
                }
            }

            //Start Keepalive thread
            var keepalivethread = new Thread(KeepAliveScheduler);
            keepalivethread.Start();
        }

        public static void Halt()
        {
            Log.Info("Told to stop.");
            Environment.Exit(1);
        }
        protected override void OnStart(string[] args)
        {
            Start();
            base.OnStart(args);
        }

        protected override void OnStop()
        {
            Log.Info("Service OnStop called: Shutting Down");
            Log.Info("Attempting to obtain lock on monitor");
            lock (MonitorObject)
            {
                Log.Info("lock obtained");
                _quitloop = true;
                Monitor.Pulse(MonitorObject);
            }
            base.OnStop();
        }
        private static void KeepAliveScheduler()
        {
            var connectionFactory = new ConnectionFactory { HostName = "gcslb01.amers1.ciscloud", Port = AmqpTcpEndpoint.UseDefaultPort };
            using (var connection = connectionFactory.CreateConnection())
            {
                using (var ch = connection.CreateModel())
                {
                    Log.Debug("Starting keepalive scheduler thread");
                    while (!_quitloop)
                    {
                        if (connection.IsOpen)
                        {
                            var payload = _configsettings;
                            payload["timestamp"] = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds;
                            Log.Debug("Publishing keepalive");
                            var properties = new BasicProperties { ContentType = "application/json" };
                            ch.BasicPublish("amq.direct", "keepalives", properties, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
                        }
                        Thread.Sleep(KeepAliveTimeout);
                    }
                }
            }
        }


    }
}
