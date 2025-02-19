using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;
using ArmoniK.Api.Common.Channel.Utils;
using ArmoniK.Api.Common.Options;
using ArmoniK.Api.Common.Utils;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.Worker.Worker;
using Microsoft.Extensions.Logging;

namespace ArmoniK.Samples.MonteCarloSimulation.Worker
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

    public class MonteCarloWorker : WorkerStreamWrapper
    {
        private readonly ILogger<MonteCarloWorker> logger_;
        private readonly Random random_;

        public MonteCarloWorker(ILoggerFactory loggerFactory,
                               ComputePlane computePlane,
                               GrpcChannelProvider provider)
            : base(loggerFactory, computePlane, provider)
        {
            logger_ = loggerFactory.CreateLogger<MonteCarloWorker>();
            random_ = new Random();
        }

        private double GenerateRandomNormal()
        {
            double u1 = 1.0 - random_.NextDouble();
            double u2 = 1.0 - random_.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private double SimulateAssetPrice(Asset asset, double riskFreeRate, double timeHorizon)
        {
            double drift = (riskFreeRate - 0.5 * Math.Pow(asset.Volatility, 2)) * timeHorizon;
            double diffusion = asset.Volatility * Math.Sqrt(timeHorizon) * GenerateRandomNormal();
            return asset.InitialPrice * Math.Exp(drift + diffusion);
        }

        public override async Task<Output> Process(ITaskHandler taskHandler)
        {
            using var scopedLog = logger_.BeginNamedScope("Execute Monte Carlo simulation",
                                                        ("sessionId", taskHandler.SessionId),
                                                        ("taskId", taskHandler.TaskId));
            try
            {
                var parameters = JsonSerializer.Deserialize<SimulationParameters>(
                    Encoding.UTF8.GetString(taskHandler.Payload));

                double totalBasketValue = 0.0;

                // Run simulations
                for (int sim = 0; sim < parameters.SimulationsPerTask; sim++)
                {
                    double basketValue = 0.0;
                    foreach (var asset in parameters.Assets)
                    {
                        double finalPrice = SimulateAssetPrice(asset, 
                                                             parameters.RiskFreeRate,
                                                             parameters.TimeHorizon);
                        basketValue += finalPrice * asset.Weight;
                    }
                    totalBasketValue += basketValue;
                }

                // Calculate average basket value
                double averageBasketValue = totalBasketValue / parameters.SimulationsPerTask;

                var resultId = taskHandler.ExpectedResults.Single();
                await taskHandler.SendResult(resultId,
                                          Encoding.UTF8.GetBytes(averageBasketValue.ToString()))
                                          .ConfigureAwait(false);

                return new Output { Ok = new Empty() };
            }
            catch (Exception e)
            {
                logger_.LogError(e, "Error during Monte Carlo simulation.");
                return new Output
                {
                    Error = new Output.Types.Error
                    {
                        Details = e.Message,
                    },
                };
            }
        }
    }
}