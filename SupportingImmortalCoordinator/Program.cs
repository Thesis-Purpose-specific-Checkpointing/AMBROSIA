using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Ambrosia;
using CRA.ClientLibrary;

namespace SupportingImmortalCoordinator
{
    class Program
    {
        enum LogStorageOptions
        {
            Files,
            Blobs
        }

        // Base params
        private static string _instanceName;
        private static int _port = -1;
        private static string _ipAddress;
        private static string _secureNetworkAssemblyName;
        private static string _secureNetworkClassName;
        private static bool _isActiveActive = false;
        private static int _replicaNumber = 0;
        private static LogStorageOptions _logStorageType = LogStorageOptions.Files;
        
        // Time Travel Debugging params
        private static long _checkpointToLoad;
        private static long _subCheckpointToLoad = -1;
        private static int _currentVersion;
        private static bool _isTestingUpgrade;
        
        // Checkpointing Strategy params
        private static Dictionary<string, object> additionalCheckpointingParams = new Dictionary<string, object>();
        private static string _pluginPath = "";
        
        public static void main(string[] args)
        {
            var firstArg = args[0];

            // If the IC should be startet for TTD
            if (firstArg.Equals("DebugInstance"))
            {
                GenericLogsInterface.SetToGenericLogs();
                ParseAndValidateOptions(args.Skip(1).ToArray());
                var myRuntime = new AmbrosiaRuntime();
                myRuntime.InitializeRepro(_instanceName, StartupParamOverrides.ICLogLocation, _checkpointToLoad, _currentVersion,
                    _isTestingUpgrade, _subCheckpointToLoad, StartupParamOverrides.receivePort, StartupParamOverrides.sendPort,
                    StartupParamOverrides.ICCheckpointLocation, StartupParamOverrides.ICProjectLocation, _pluginPath, additionalCheckpointingParams);
                return;
            }
            
            ParseAndValidateOptions(args);

            switch (_logStorageType)
            {
                case LogStorageOptions.Files:
                    GenericLogsInterface.SetToGenericLogs();
                    break;
                case LogStorageOptions.Blobs:
                    AzureBlobsLogsInterface.SetToAzureBlobsLogs();
                    break;
            }

            var replicaName = $"{_instanceName}{_replicaNumber}";

            if (_ipAddress == null)
            {
                _ipAddress = GetLocalIPAddress();
            }

            string storageConnectionString = null;

            if (storageConnectionString == null)
            {
                storageConnectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONN_STRING");
            }

            if (!_isActiveActive && _replicaNumber != 0)
            {
                throw new InvalidOperationException("Can't specify a replica number without the activeActive flag");
            }

            if (storageConnectionString == null)
            {
                throw new InvalidOperationException("Azure storage connection string not found. Use appSettings in your app.config to provide this using the key AZURE_STORAGE_CONN_STRING, or use the environment variable AZURE_STORAGE_CONN_STRING.");
            }

            int connectionsPoolPerWorker;
            string connectionsPoolPerWorkerString = "0";
            if (connectionsPoolPerWorkerString != null)
            {
                try
                {
                    connectionsPoolPerWorker = Convert.ToInt32(connectionsPoolPerWorkerString);
                }
                catch
                {
                    throw new InvalidOperationException("Maximum number of connections per CRA worker is wrong. Use appSettings in your app.config to provide this using the key CRA_WORKER_MAX_CONN_POOL.");
                }
            }
            else
            {
                connectionsPoolPerWorker = 1000;
            }

            ISecureStreamConnectionDescriptor descriptor = null;
            if (_secureNetworkClassName != null)
            {
                Type type;
                if (_secureNetworkAssemblyName != null)
                {
                    var assembly = Assembly.Load(_secureNetworkAssemblyName);
                    type = assembly.GetType(_secureNetworkClassName);
                }
                else
                {
                    type = Type.GetType(_secureNetworkClassName);
                }
                descriptor = (ISecureStreamConnectionDescriptor)Activator.CreateInstance(type);
            }

            var dataProvider = new CRA.DataProvider.Azure.AzureDataProvider(storageConnectionString);
            var worker = new CRAWorker
                (replicaName, _ipAddress, _port,
                dataProvider, descriptor, connectionsPoolPerWorker);

            worker.DisableDynamicLoading(); 
            worker.SideloadVertex(new AmbrosiaRuntime(), "ambrosia");

            worker.Start();
        }

        static void Main(string[] args)
        {
            Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
            main(args);
        }

        private static string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            throw new InvalidOperationException("Local IP Address Not Found!");
        }

        private static void ParseAndValidateOptions(string[] args)
        {
            var options = ParseOptions(args, out var shouldShowHelp);
            ValidateOptions(options, shouldShowHelp);
        }

        private static OptionSet ParseOptions(string[] args, out bool shouldShowHelp)
        {
            var showHelp = false;
            var options = new OptionSet {
                { "i|instanceName=", "The instance name [REQUIRED].", n => _instanceName = n },
                { "p|port=", "An port number [REQUIRED].", p => _port = Int32.Parse(p) },
                {"aa|activeActive", "Is active-active enabled.", aa => _isActiveActive = true},
                { "r|replicaNum=", "The replica #", r => { _replicaNumber = int.Parse(r); _isActiveActive=true; } },
                { "an|assemblyName=", "The secure network assembly name.", an => _secureNetworkAssemblyName = an },
                { "ac|assemblyClass=", "The secure network assembly class.", ac => _secureNetworkClassName = ac },
                { "ip|IPAddr=", "Override automatic self IP detection", i => _ipAddress = i },
                { "h|help", "show this message and exit", h => showHelp = h != null },
                { "rp|receivePort=", "The service receive from port override.", rp => StartupParamOverrides.receivePort = int.Parse(rp) },
                { "sp|sendPort=", "The service send to port override.", sp => StartupParamOverrides.sendPort = int.Parse(sp) },
                { "l|log=", "The service log path override.", l => StartupParamOverrides.ICLogLocation = l},
                { "cp|cachePath=", "The service cache path override.", cp => StartupParamOverrides.ICCacheLocation = cp },
                { "lts|logTriggerSize=", "Log trigger size (in MBs).", lts => StartupParamOverrides.LogTriggerSizeMB = long.Parse(lts) },
                { "lst|logStorageType=", "Can be set to files or blobs. Defaults to files", lst => _logStorageType = (LogStorageOptions) Enum.Parse(typeof(LogStorageOptions), lst, true)},
               
                // Time Travel Debugging params
                { "c|checkpoint=", "The checkpoint # to load.", c => _checkpointToLoad = long.Parse(c) },
                { "sc|subcheckpoint=", "The sub-checkpoint # to load.", sc => _subCheckpointToLoad = long.Parse(sc) },
                { "cv|currentVersion=", "The version # to debug.", cv => _currentVersion = int.Parse(cv) },
                { "tu|testingUpgrade", "Is testing upgrade.", u => _isTestingUpgrade = true },
                { "cpp|checkpointPath=", "The service checkpoint path override.", cpp => StartupParamOverrides.ICCheckpointLocation = cpp },
                { "pp|projectPath=", "The service project path override.", pp => StartupParamOverrides.ICProjectLocation = pp },
                
                // Strategy params
                { "plugp|pluginPath=", "The path to an additional folder, which includes further dlls that should be loaded dynamically.", plugp => _pluginPath = plugp },
            };

            try
            {
                var extra = options.Parse(args);
                foreach (var extraArgument in extra)
                {
                    var positionAfterMinus = extraArgument.LastIndexOf("--") + 2;
                    var positionOfEqual = extraArgument.IndexOf('=');

                    var name = extraArgument.Substring(positionAfterMinus, positionOfEqual - positionAfterMinus);
                    var value = extraArgument.Substring(positionOfEqual + 1);
                    
                    additionalCheckpointingParams.Add(name, value);
                }
            }
            catch (OptionException e)
            {
                Console.WriteLine("Invalid arguments: " + e.Message);
                ShowHelp(options);
                Environment.Exit(1);
            }

            shouldShowHelp = showHelp;

            return options;
        }

        private static void ValidateOptions(OptionSet options, bool shouldShowHelp)
        {
            var errorMessage = string.Empty;
            if (_instanceName == null) errorMessage += "Instance name is required.";
            if (_port == -1) errorMessage += "Port number is required.";

            if (errorMessage != string.Empty)
            {
                Console.WriteLine(errorMessage);
                ShowHelp(options);
                Environment.Exit(1);
            }

            if (shouldShowHelp) ShowHelp(options);
        }

        private static void ShowHelp(OptionSet options)
        {
            var name = typeof(Program).Assembly.GetName().Name;
            Console.WriteLine("Worker for Common Runtime for Applications (CRA) [http://github.com/Microsoft/CRA]");
#if NETCORE
            Console.WriteLine($"Usage: dotnet {name}.dll [OPTIONS]\nOptions:");
#else
            Console.WriteLine($"Usage: {name}.exe [OPTIONS]\nOptions:");
#endif

            options.WriteOptionDescriptions(Console.Out);
            Environment.Exit(0);
        }

    }
}