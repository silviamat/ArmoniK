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
using ArmoniK.Api.Worker.Worker;
using Microsoft.Extensions.Logging;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.CommandLine;
using System.Threading;
using System.Security.Cryptography;

namespace BasketSimulation
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

    public class BasketSimulationWorker : WorkerStreamWrapper
    {
        private readonly ILogger<BasketSimulationWorker> logger_;

        public BasketSimulationWorker(ILoggerFactory loggerFactory, ComputePlane computePlane, GrpcChannelProvider provider)
            : base(loggerFactory, computePlane, provider)
            => logger_ = loggerFactory.CreateLogger<BasketSimulationWorker>();

        public override async Task<Output> Process(ITaskHandler taskHandler)
        {
            using var scopedLog = logger_.BeginNamedScope("Execute task", ("sessionId", taskHandler.SessionId), ("taskId", taskHandler.TaskId));
            try
            {
                var input = Encoding.ASCII.GetString(taskHandler.Payload);
                var resultId = taskHandler.ExpectedResults.Single();
                var simulator = new BasketSimulator();
                var basket = new List<Asset>
                {
                    new Asset { Name = "AAPL", Spot = 180.0, Volatility = 0.25, Weight = 0.4 },
                    new Asset { Name = "MSFT", Spot = 350.0, Volatility = 0.20, Weight = 0.3 },
                    new Asset { Name = "GOOGL", Spot = 140.0, Volatility = 0.28, Weight = 0.3 }
                };
                double riskFreeRate = 0.05;
                double timeToMaturity = 1.0;
                int numPaths = 10000;
                double basketValue = simulator.SimulateBasketValue(basket, riskFreeRate, timeToMaturity, numPaths);
                await taskHandler.SendResult(resultId, Encoding.ASCII.GetBytes(basketValue.ToString())).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger_.LogError(e, "Error during task computing.");
                return new Output { Error = new Output.Types.Error { Details = e.Message } };
            }
            return new Output { Ok = new Empty() };
        }
    }
}
