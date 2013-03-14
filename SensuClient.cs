﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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
        private static bool _safemode;
        private static readonly List<string> _checksInProgress = new List<string>();
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
            try
            {
                bool.TryParse(_configsettings["client"]["safemode"].ToString(), out _safemode);
            }
            catch (NullReferenceException)
            {
            }
            //Start Keepalive thread
            var keepalivethread = new Thread(KeepAliveScheduler);
            keepalivethread.Start();

            //Start subscriptions thread.
            var subscriptionsthread = new Thread(Subscriptions);
            subscriptionsthread.Start();
        }

        private static void Subscriptions()
        {
            Log.Debug("Subscribing to client subscriptions");

            using (var ch = GetRabbitConnection().CreateModel())
            {
                var q = ch.QueueDeclare("", false, false, true, null);
                foreach (var subscription in _configsettings["client"]["subscriptions"])
                {
                    Log.Debug("Binding queue {0} to exchange {1}", q.QueueName, subscription);
                    ch.QueueBind(q.QueueName, subscription.ToString(), "");
                }
                var consumer = new QueueingBasicConsumer(ch);
                ch.BasicConsume(q.QueueName, true, consumer);
                while (true)
                {
                    object msg = null;
                    consumer.Queue.Dequeue(100, out msg);
                    if (msg != null)
                    {
                        var payload = Encoding.UTF8.GetString(((BasicDeliverEventArgs)msg).Body);
                        try
                        {
                            var check = JObject.Parse(payload);
                            Log.Debug("Received check request: {0}", check.ToString());
                            ProcessCheck(check);
                        }
                        catch (JsonReaderException ex)
                        {
                            Log.Error("Malformed Check: {0}", payload);
                        }
                    }
                    //Lets us quit while we're still sleeping.
                    lock (MonitorObject)
                    {
                        if (_quitloop)
                        {
                            Log.Warn("Quitloop set, exiting main loop");
                            break;
                        }
                        Monitor.Wait(MonitorObject, KeepAliveTimeout);
                        if (_quitloop)
                        {
                            Log.Warn("Quitloop set, exiting main loop");
                            break;
                        }
                    }
                }
            }

        }

        private static void ProcessCheck(JObject check)
        {
            Log.Debug("Processing check {0}", check.ToString());
            JToken command;
            if (check.TryGetValue("command", out command))
            {
                if (_configsettings["check"] != null && _configsettings["check"].Contains(check["name"]))
                {
                    foreach (var thingie in _configsettings["checks"][check["name"]])
                    {
                        check.Add(thingie);
                    }
                    ExecuteCheckCommand(check);
                }
                else if (_safemode)
                {
                    check["output"] = "Check is not locally defined (safemode)";
                    check["status"] = 3;
                    check["handle"] = false;
                    PublishResult(check);
                }
                else
                {
                    ExecuteCheckCommand(check);
                }
            }
            else
            {
                Log.Warn("Unknown check exception: {0}", check);
            }
        }

        private static void PublishResult(JObject check)
        {
            var payload = new JObject();
            payload["check"] = check;
            payload["client"] = _configsettings["client"]["name"];
            Log.Info("Publishing Check {0}", payload);
            using (var ch = GetRabbitConnection().CreateModel())
            {
                var properties = new BasicProperties
                {
                    ContentType = "application/octet-stream",
                    Priority = 0,
                    DeliveryMode = 1
                };
                ch.BasicPublish("", "results", properties, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
            }
            _checksInProgress.Remove(check["name"].ToString());
        }

        private static void ExecuteCheckCommand(JObject check)
        {
            Log.Debug("Attempting to execute check command {0}", check);
            if (!_checksInProgress.Contains(check["name"].ToString()))
            {
                _checksInProgress.Add(check["name"].ToString());
                List<string> unmatchedTokens;
                var command = SubstitueCommandTokens(check, out unmatchedTokens);
                if (unmatchedTokens == null || unmatchedTokens.Count == 0)
                {
                    var processstartinfo = new ProcessStartInfo(command)
                        {
                            WindowStyle = ProcessWindowStyle.Hidden,
                            UseShellExecute = false,
                            RedirectStandardError = true,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true
                        };
                    var process = new Process { StartInfo = processstartinfo };
                    var stopwatch = new Stopwatch();
                    try
                    {
                        stopwatch.Start();
                        process.Start();
                        if (!process.WaitForExit(1000 * int.Parse(check["timeout"].ToString())))
                        {
                            process.Kill();
                        }
                        check["output"] = string.Format("{0}{1}", process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd());
                        check["status"] = process.ExitCode;
                    }
                    catch (Win32Exception ex)
                    {
                        check["output"] = string.Format("Unexpected error: {0}", ex.Message);
                        check["status"] = 2;
                    }
                    stopwatch.Stop();
                    check["duration"] = string.Format("{0:.000}", stopwatch.ElapsedMilliseconds / 1000);
                    PublishResult(check);
                }
                else
                {
                    check["output"] = string.Format("Unmatched command tokens: {0}",
                                                    string.Join(",", unmatchedTokens.ToArray()));
                    check["status"] = 3;
                    check["handle"] = false;
                    PublishResult(check);
                    _checksInProgress.Remove(check["name"].ToString());
                }
            }
            else
            {
                Log.Warn("Previous check command execution in progress {0}", check["command"]);
            }
        }

        private static string SubstitueCommandTokens(JObject check, out List<string> unmatchedTokens)
        {
            var temptokens = new List<string>();
            var command = check["command"].ToString();
            var blah = new Regex(":::(.*?):::", RegexOptions.Compiled);
            command = blah.Replace(command, match =>
                {
                    var matched = "";
                    foreach (var p in match.Value.Split('.'))
                    {
                        if (_configsettings["client"][p] != null)
                        {
                            matched += _configsettings["client"][p];
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(matched)) { temptokens.Add(match.Value); }
                    return matched;
                });
            unmatchedTokens = temptokens;
            return command;
        }

        private static void KeepAliveScheduler()
        {

            using (var ch = GetRabbitConnection().CreateModel())
            {
                Log.Debug("Starting keepalive scheduler thread");
                while (true)
                {
                    var payload = _configsettings["client"];
                    payload["timestamp"] = Convert.ToInt64(Math.Round((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds, MidpointRounding.AwayFromZero));
                    Log.Debug("Publishing keepalive");
                    var properties = new BasicProperties
                        {
                            ContentType = "application/octet-stream",
                            Priority = 0,
                            DeliveryMode = 1
                        };
                    ch.BasicPublish("", "keepalives", properties, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));

                    //Lets us quit while we're still sleeping.
                    lock (MonitorObject)
                    {
                        if (_quitloop)
                        {
                            Log.Warn("Quitloop set, exiting main loop");
                            break;
                        }
                        Monitor.Wait(MonitorObject, KeepAliveTimeout);
                        if (_quitloop)
                        {
                            Log.Warn("Quitloop set, exiting main loop");
                            break;
                        }
                    }
                }
            }
        }
        public static void Halt()
        {
            Log.Info("Told to stop. Obeying.");
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
        private static IConnection rabbitMqConnection = null;
        private static IConnection GetRabbitConnection()
        {
            if (rabbitMqConnection == null || !rabbitMqConnection.IsOpen)
            {
                var connectionFactory = new ConnectionFactory
                    {
                        HostName = _configsettings["rabbitmq"]["host"].ToString(),
                        Port = int.Parse(_configsettings["rabbitmq"]["port"].ToString()),
                        UserName = _configsettings["rabbitmq"]["user"].ToString(),
                        Password = _configsettings["rabbitmq"]["password"].ToString(),
                        VirtualHost = _configsettings["rabbitmq"]["vhost"].ToString()
                    };
                rabbitMqConnection = connectionFactory.CreateConnection();
            }
            return rabbitMqConnection;
        }
    }
}
