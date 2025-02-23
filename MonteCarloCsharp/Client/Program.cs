using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArmoniK.Api.Client;
using ArmoniK.Api.Client.Options;
using ArmoniK.Api.Client.Submitter;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.gRPC.V1.Results;
using ArmoniK.Api.gRPC.V1.Sessions;
using ArmoniK.Api.gRPC.V1.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.CommandLine;
using System.Threading;
using System.Security.Cryptography;

namespace BasketSimulation.Client
{
    internal class Asset
    {
        public string Name { get; set; }
        public double Spot { get; set; }
        public double Volatility { get; set; }
        public double Weight { get; set; }
    }

    internal class BasketSimulator
    {
        private readonly Random random = new Random();
        
        public double SimulateBasketValue(List<Asset> basket, double riskFreeRate, double timeToMaturity, int numPaths)
        {
            double totalValue = 0.0;

            for (int path = 0; path < numPaths; path++)
            {
                double pathValue = 0.0;
                foreach (var asset in basket)
                {
                    double S0 = asset.Spot;
                    double sigma = asset.Volatility;
                    double weight = asset.Weight;
                    double Z = GetNormalRandom();

                    double ST = S0 * Math.Exp((riskFreeRate - 0.5 * sigma * sigma) * timeToMaturity + sigma * Math.Sqrt(timeToMaturity) * Z);
                    pathValue += weight * ST;
                }
                totalValue += pathValue;
            }
            return Math.Exp(-riskFreeRate * timeToMaturity) * (totalValue / numPaths);
        }

        private double GetNormalRandom()
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                byte[] bytes = new byte[8];
                rng.GetBytes(bytes);
                double u1 = BitConverter.ToUInt64(bytes, 0) / (double)ulong.MaxValue;
                double u2 = BitConverter.ToUInt64(bytes, 4) / (double)ulong.MaxValue;
                return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            }
        }
    }

    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var endpoint = new Option<string>("--endpoint", "ArmoniK control plane endpoint", () => "http://localhost:5001");
            var partition = new Option<string>("--partition", "Partition to submit tasks", () => "default");
            
            var rootCommand = new RootCommand("Monte Carlo Basket Simulation with ArmoniK");
            rootCommand.AddOption(endpoint);
            rootCommand.AddOption(partition);
            rootCommand.SetHandler(async (endpoint, partition) => await Run(endpoint, partition), endpoint, partition);
            
            return await rootCommand.InvokeAsync(args);
        }

        private static async Task Run(string endpoint, string partition)
        {
            var channel = GrpcChannelFactory.CreateChannel(new GrpcClient { Endpoint = endpoint });
            var sessionClient = new Sessions.SessionsClient(channel);
            var taskClient = new Tasks.TasksClient(channel);
            var resultClient = new Results.ResultsClient(channel);
            
            var taskOptions = new TaskOptions { MaxDuration = Duration.FromTimeSpan(TimeSpan.FromHours(1)), PartitionId = partition };
            var createSessionReply = sessionClient.CreateSession(new CreateSessionRequest { DefaultTaskOption = taskOptions, PartitionIds = { partition } });
            string sessionId = createSessionReply.SessionId;
            
            var resultId = resultClient.CreateResultsMetaData(new CreateResultsMetaDataRequest { SessionId = sessionId, Results = { new CreateResultsMetaDataRequest.Types.ResultCreate { Name = "BasketValue" } } }).Results.Single().ResultId;
            
            var payload = Encoding.ASCII.GetBytes("BasketSimulation");
            var payloadId = resultClient.CreateResults(new CreateResultsRequest { SessionId = sessionId, Results = { new CreateResultsRequest.Types.ResultCreate { Data = UnsafeByteOperations.UnsafeWrap(payload), Name = "Payload" } } }).Results.Single().ResultId;
            
            var submitResponse = taskClient.SubmitTasks(new SubmitTasksRequest { SessionId = sessionId, TaskCreations = { new SubmitTasksRequest.Types.TaskCreation { PayloadId = payloadId, ExpectedOutputKeys = { resultId } } } });
            string taskId = submitResponse.TaskInfos.Single().TaskId;
            
            await Task.Delay(5000);
            var result = await resultClient.DownloadResultData(sessionId, resultId, CancellationToken.None);
            Console.WriteLine("Simulated Basket Value: " + Encoding.ASCII.GetString(result));
        }
    }
}
