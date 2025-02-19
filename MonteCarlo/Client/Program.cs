using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using ArmoniK.Api.Client;
using ArmoniK.Api.Client.Options;
using ArmoniK.Api.Client.Submitter;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.gRPC.V1.Events;
using ArmoniK.Api.gRPC.V1.Results;
using ArmoniK.Api.gRPC.V1.Sessions;
using ArmoniK.Api.gRPC.V1.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using static System.Console;

namespace ArmoniK.Samples.MonteCarloSimulation.Client
{
    public class Asset
    {
        public string Symbol { get; set; }
        public double InitialPrice { get; set; }
        public double Volatility { get; set; }
        public double Weight { get; set; }
    }

    public class SimulationParameters
    {
        public List<Asset> Assets { get; set; }
        public double RiskFreeRate { get; set; }
        public double TimeHorizon { get; set; }
        public int SimulationsPerTask { get; set; }
    }

    internal static class Program
    {
        internal static async Task Run(string endpoint,
                                     string partition,
                                     int numberOfTasks)
        {
            var channel = GrpcChannelFactory.CreateChannel(new GrpcClient { Endpoint = endpoint });
            var taskClient = new Tasks.TasksClient(channel);
            var resultClient = new Results.ResultsClient(channel);
            var sessionClient = new Sessions.SessionsClient(channel);
            var eventClient = new Events.EventsClient(channel);

            var taskOptions = new TaskOptions
            {
                MaxDuration = Duration.FromTimeSpan(TimeSpan.FromHours(1)),
                MaxRetries = 2,
                Priority = 1,
                PartitionId = partition,
            };

            var createSessionReply = sessionClient.CreateSession(new CreateSessionRequest
            {
                DefaultTaskOption = taskOptions,
                PartitionIds = { partition },
            });

            WriteLine($"Session ID: {createSessionReply.SessionId}");

            // Create simulation parameters
            var simParams = new SimulationParameters
            {
                Assets = new List<Asset>
                {
                    new Asset { Symbol = "AAPL", InitialPrice = 150.0, Volatility = 0.25, Weight = 0.4 },
                    new Asset { Symbol = "MSFT", InitialPrice = 280.0, Volatility = 0.22, Weight = 0.3 },
                    new Asset { Symbol = "GOOGL", InitialPrice = 2500.0, Volatility = 0.28, Weight = 0.3 }
                },
                RiskFreeRate = 0.02,
                TimeHorizon = 1.0,
                SimulationsPerTask = 10000
            };

            var resultIds = new List<string>();
            var tasks = new List<SubmitTasksRequest.Types.TaskCreation>();

            // Create tasks
            for (int i = 0; i < numberOfTasks; i++)
            {
                var resultId = resultClient.CreateResultsMetaData(new CreateResultsMetaDataRequest
                {
                    SessionId = createSessionReply.SessionId,
                    Results = { new CreateResultsMetaDataRequest.Types.ResultCreate { Name = $"Result_{i}" } }
                }).Results.Single().ResultId;

                resultIds.Add(resultId);

                var payloadId = resultClient.CreateResults(new CreateResultsRequest
                {
                    SessionId = createSessionReply.SessionId,
                    Results = { new CreateResultsRequest.Types.ResultCreate
                    {
                        Data = UnsafeByteOperations.UnsafeWrap(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(simParams))),
                        Name = $"Payload_{i}",
                    }}
                }).Results.Single().ResultId;

                tasks.Add(new SubmitTasksRequest.Types.TaskCreation
                {
                    PayloadId = payloadId,
                    ExpectedOutputKeys = { resultId },
                });
            }

            var submitTasksResponse = taskClient.SubmitTasks(new SubmitTasksRequest
            {
                SessionId = createSessionReply.SessionId,
                TaskCreations = { tasks },
            });

            WriteLine($"Submitted {tasks.Count} tasks");

            // Wait for all results
            await eventClient.WaitForResultsAsync(createSessionReply.SessionId,
                                                resultIds,
                                                CancellationToken.None);

            // Aggregate results
            double totalValue = 0.0;
            foreach (var resultId in resultIds)
            {
                var result = await resultClient.DownloadResultData(createSessionReply.SessionId,
                                                                 resultId,
                                                                 CancellationToken.None);
                totalValue += double.Parse(Encoding.UTF8.GetString(result));
            }

            var finalValue = totalValue / numberOfTasks;
            WriteLine($"Estimated basket value: {finalValue:C2}");
        }

        public static async Task<int> Main(string[] args)
        {
            var endpoint = new Option<string>("--endpoint",
                                            description: "Endpoint for the connection to ArmoniK control plane.",
                                            getDefaultValue: () => "http://localhost:5001");
            var partition = new Option<string>("--partition",
                                             description: "Name of the partition to which submit tasks.",
                                             getDefaultValue: () => "default");
            var tasks = new Option<int>("--tasks",
                                      description: "Number of Monte Carlo simulation tasks to run.",
                                      getDefaultValue: () => 10);

            var rootCommand = new RootCommand("Monte Carlo simulation for basket of assets valuation using ArmoniK.");
            rootCommand.AddOption(endpoint);
            rootCommand.AddOption(partition);
            rootCommand.AddOption(tasks);

            rootCommand.SetHandler(Run, endpoint, partition, tasks);

            return await rootCommand.InvokeAsync(args);
        }
    }
}