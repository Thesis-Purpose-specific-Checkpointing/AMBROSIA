﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.IO;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.VisualStudio.Threading;
using Microsoft.WindowsAzure.Storage.Queue;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.CompilerServices;
using CRA.ClientLibrary;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Serialization;

namespace Ambrosia
{
    class Program
    {
        private static LocalAmbrosiaRuntimeModes _runtimeMode;
        private static string _instanceName = null;
        private static int _replicaNumber = 0;
        private static int _serviceReceiveFromPort = -1;
        private static int _serviceSendToPort = -1;
        private static string _serviceLogPath = Path.Combine(Path.GetPathRoot(Path.GetFullPath(".")), "AmbrosiaLogs") + Path.DirectorySeparatorChar;
        private static string _serviceCheckpointPath;
        private static string _serviceProjectPath;
        private static string _binariesLocation = "AmbrosiaBinaries";
        private static long _checkpointToLoad = 1;
        private static bool _isTestingUpgrade = false;
        private static AmbrosiaRecoveryModes _recoveryMode = AmbrosiaRecoveryModes.A;
        private static bool _isActiveActive = false;
        private static bool _isPauseAtStart = false;
        private static bool _isPersistLogs = true;
        private static long _logTriggerSizeMB = 1000;
        private static int _currentVersion = 0;
        private static long _upgradeVersion = -1;

        static void Main(string[] args)
        {
            GenericLogsInterface.SetToGenericLogs();
            ParseAndValidateOptions(args);

            Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));

            switch (_runtimeMode)
            {
                case LocalAmbrosiaRuntimeModes.DebugInstance:
                    var myRuntime = new AmbrosiaRuntime();
                    myRuntime.InitializeRepro(_instanceName, _serviceLogPath, _checkpointToLoad, _currentVersion,
                        _isTestingUpgrade, _serviceReceiveFromPort, _serviceSendToPort);
                    return;
                case LocalAmbrosiaRuntimeModes.AddReplica:
                case LocalAmbrosiaRuntimeModes.RegisterInstance:
                    if (_runtimeMode == LocalAmbrosiaRuntimeModes.AddReplica)
                    {
                        _isActiveActive = true;
                    }

                    var dataProvider = new CRA.DataProvider.Azure.AzureDataProvider(Environment.GetEnvironmentVariable("AZURE_STORAGE_CONN_STRING"));
                    var client = new CRAClientLibrary(dataProvider);
                    client.DisableArtifactUploading();

                    var replicaName = $"{_instanceName}{_replicaNumber}";
                    AmbrosiaRuntimeParams param = new AmbrosiaRuntimeParams();
                    param.createService = _recoveryMode == AmbrosiaRecoveryModes.A
                        ? (bool?)null
                        : (_recoveryMode != AmbrosiaRecoveryModes.N);
                    param.pauseAtStart = _isPauseAtStart;
                    param.persistLogs = _isPersistLogs;
                    param.logTriggerSizeMB = _logTriggerSizeMB;
                    param.activeActive = _isActiveActive;
                    param.upgradeToVersion = _upgradeVersion;
                    param.currentVersion = _currentVersion;
                    param.serviceReceiveFromPort = _serviceReceiveFromPort;
                    param.serviceSendToPort = _serviceSendToPort;
                    param.serviceName = _instanceName;
                    param.serviceLogPath = _serviceLogPath;
                    param.serviceCheckpointPath = _serviceCheckpointPath;
                    param.serviceProjectPath = _serviceProjectPath;
                    param.AmbrosiaBinariesLocation = _binariesLocation;
                    param.storageConnectionString = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONN_STRING");

                    try
                    {
                        if (client.DefineVertexAsync(param.AmbrosiaBinariesLocation, () => new AmbrosiaRuntime()).GetAwaiter().GetResult() != CRAErrorCode.Success)
                        {
                            throw new Exception();
                        }

                        // Workaround because of limitation in parameter serialization in CRA
                        XmlSerializer xmlSerializer = new XmlSerializer(param.GetType());
                        string serializedParams;
                        using (StringWriter textWriter = new StringWriter())
                        {
                            xmlSerializer.Serialize(textWriter, param);
                            serializedParams = textWriter.ToString();
                        }

                        if (client.InstantiateVertexAsync(replicaName, param.serviceName, param.AmbrosiaBinariesLocation, serializedParams).GetAwaiter().GetResult() != CRAErrorCode.Success)
                        {
                            throw new Exception();
                        }
                        client.AddEndpointAsync(param.serviceName, AmbrosiaRuntime.AmbrosiaDataInputsName, true, true).Wait();
                        client.AddEndpointAsync(param.serviceName, AmbrosiaRuntime.AmbrosiaDataOutputsName, false, true).Wait();
                        client.AddEndpointAsync(param.serviceName, AmbrosiaRuntime.AmbrosiaControlInputsName, true, true).Wait();
                        client.AddEndpointAsync(param.serviceName, AmbrosiaRuntime.AmbrosiaControlOutputsName, false, true).Wait();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Error trying to upload service. Exception: " + e.Message);
                    }

                    return;
                default:
                    throw new NotSupportedException($"Runtime mode: {_runtimeMode} not supported.");
            }
        }

        private static void ParseAndValidateOptions(string[] args)
        {
            var options = ParseOptions(args, out var shouldShowHelp);
            ValidateOptions(options, shouldShowHelp);
        }

        private static OptionSet ParseOptions(string[] args, out bool shouldShowHelp)
        {
            var showHelp = false;

            var basicOptions = new OptionSet
            {
                { "i|instanceName=", "The instance name [REQUIRED].", i => _instanceName = i },
                { "rp|receivePort=", "The service receive from port [REQUIRED].", rp => _serviceReceiveFromPort = int.Parse(rp) },
                { "sp|sendPort=", "The service send to port. [REQUIRED]", sp => _serviceSendToPort = int.Parse(sp) },
                { "l|log=", "The service log path.", l => _serviceLogPath = l },
            };

            var helpOption = new OptionSet
            {
                {"h|help", "show this message and exit", h => showHelp = h != null},
            };

            var registerInstanceOptionSet = basicOptions.AddMany(new OptionSet
            {
                {
                    "cs|createService=",
                    $"[{string.Join(" | ", GetModesDescriptions().Select(md => $"{md.Item1} - {md.Item2}"))}].",
                    cs => _recoveryMode = (AmbrosiaRecoveryModes) Enum.Parse(typeof(AmbrosiaRecoveryModes), cs, true)
                },
                {"ps|pauseAtStart", "Is pause at start enabled.", ps => _isPauseAtStart = true},
                {"npl|noPersistLogs", "Is persistent logging disabled.", ps => _isPersistLogs = false},
                {"lts|logTriggerSize=", "Log trigger size (in MBs).", lts => _logTriggerSizeMB = long.Parse(lts)},
                {"aa|activeActive", "Is active-active enabled.", aa => _isActiveActive = true},
                {"cv|currentVersion=", "The current version #.", cv => _currentVersion = int.Parse(cv)},
                {"uv|upgradeVersion=", "The upgrade version #.", uv => _upgradeVersion = int.Parse(uv)},
                { "cpp|checkpointPath=", "The service checkpoint path override.", cpp => _serviceCheckpointPath = cpp },
                { "pp|projectPath=", "The service project path override.", pp => _serviceProjectPath = pp },
            });

            var addReplicaOptionSet = new OptionSet {
                { "r|replicaNum=", "The replica # [REQUIRED].", r => _replicaNumber = int.Parse(r) },
            }.AddMany(registerInstanceOptionSet);

            var debugInstanceOptionSet = basicOptions.AddMany(new OptionSet {

                { "c|checkpoint=", "The checkpoint # to load.", c => _checkpointToLoad = long.Parse(c) },
                { "cv|currentVersion=", "The version # to debug.", cv => _currentVersion = int.Parse(cv) },
                { "tu|testingUpgrade", "Is testing upgrade.", u => _isTestingUpgrade = true },
            });

            registerInstanceOptionSet = registerInstanceOptionSet.AddMany(helpOption);
            addReplicaOptionSet = addReplicaOptionSet.AddMany(helpOption);
            debugInstanceOptionSet = debugInstanceOptionSet.AddMany(helpOption);


            var runtimeModeToOptionSet = new Dictionary<LocalAmbrosiaRuntimeModes, OptionSet>
            {
                { LocalAmbrosiaRuntimeModes.RegisterInstance, registerInstanceOptionSet},
                { LocalAmbrosiaRuntimeModes.AddReplica, addReplicaOptionSet},
                { LocalAmbrosiaRuntimeModes.DebugInstance, debugInstanceOptionSet},
            };

            _runtimeMode = default(LocalAmbrosiaRuntimeModes);
            if (args.Length < 1 || !Enum.TryParse(args[0], true, out _runtimeMode))
            {
                Console.WriteLine("Missing or illegal runtime mode.");
                ShowHelp(runtimeModeToOptionSet);
                Environment.Exit(1);
            }

            var options = runtimeModeToOptionSet[_runtimeMode];
            try
            {
                options.Parse(args);
            }
            catch (OptionException e)
            {
                Console.WriteLine("Invalid arguments: " + e.Message);
                ShowHelp(options, _runtimeMode);
                Environment.Exit(1);
            }

            shouldShowHelp = showHelp;

            return options;
        }

        public enum LocalAmbrosiaRuntimeModes
        {
            AddReplica,
            RegisterInstance,
            DebugInstance,
        }

        public enum AmbrosiaRecoveryModes
        {
            [Description("AutoRecovery")]
            A,
            [Description("NoRecovery")]
            N,
            [Description("AlwaysRecover")]
            Y,
        }

        private static IEnumerable<Tuple<string, string>> GetModesDescriptions()
        {
            foreach (var mode in Enum.GetValues(typeof(AmbrosiaRecoveryModes)))
            {
                yield return new Tuple<string, string>(mode.ToString(), ((Enum)mode).GetDescription());
            }
        }

        private static void ValidateOptions(OptionSet options, bool shouldShowHelp)
        {
            var errorMessage = string.Empty;
            if (_instanceName == null) errorMessage += "Instance name is required.\n";
            if (_serviceReceiveFromPort == -1) errorMessage += "Receive port is required.\n";
            if (_serviceSendToPort == -1) errorMessage += "Send port is required.\n";
            if (_runtimeMode == LocalAmbrosiaRuntimeModes.AddReplica)
            {
                if (_replicaNumber == 0)
                {
                    errorMessage += "Replica number is required.\n";
                }
            }

            // handles the case when an upgradeversion is not specified
            if (_upgradeVersion == -1)
            {
                _upgradeVersion = _currentVersion;
            }


            if (_currentVersion > _upgradeVersion)
            {
                errorMessage += "Current version # exceeds upgrade version #.\n";
            }

            if (errorMessage != string.Empty)
            {
                Console.WriteLine(errorMessage);
                ShowHelp(options, _runtimeMode);
                Environment.Exit(1);
            }


            if (shouldShowHelp)
            {
                ShowHelp(options, _runtimeMode);
                Environment.Exit(0);
            }
        }

        private static void ShowHelp(OptionSet options, LocalAmbrosiaRuntimeModes mode)
        {
            var name = typeof(Program).Assembly.GetName().Name;
#if NETCORE
            Console.WriteLine($"Usage: dotnet {name}.dll {mode} [OPTIONS]\nOptions:");
#else
            Console.WriteLine($"Usage: {name}.exe {mode} [OPTIONS]\nOptions:");
#endif
            options.WriteOptionDescriptions(Console.Out);
        }

        private static void ShowHelp(Dictionary<LocalAmbrosiaRuntimeModes, OptionSet> modeToOptions)
        {
            foreach (var modeToOption in modeToOptions)
            {
                ShowHelp(modeToOption.Value, modeToOption.Key);
            }
        }
    }

    public static class OptionSetExtensions
    {
        public static OptionSet AddMany(this OptionSet thisOptionSet, OptionSet otherOptionSet)
        {
            var newOptionSet = new OptionSet();
            foreach (var option in thisOptionSet)
            {
                newOptionSet.Add(option);
            }
            foreach (var option in otherOptionSet)
            {
                newOptionSet.Add(option);
            }

            return newOptionSet;
        }

        public static string GetDescription(this Enum value)
        {
            Type type = value.GetType();
            string enumName = Enum.GetName(type, value);
            if (enumName == null)
            {
                return null; // or return string.Empty;
            }
            var typeField = type.GetField(enumName);
            if (typeField == null)
            {
                return null; // or return string.Empty;
            }
            var attribute = Attribute.GetCustomAttribute(typeField, typeof(DescriptionAttribute));
            return (attribute as DescriptionAttribute)?.Description; // ?? string.Empty maybe added
        }
    }
}