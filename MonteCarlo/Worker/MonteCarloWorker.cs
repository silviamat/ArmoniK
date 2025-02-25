using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Collections.Generic;

using ArmoniK.Api.Common.Channel.Utils;
using ArmoniK.Api.Common.Options;
using ArmoniK.Api.Common.Utils;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.Worker.Worker;

using Microsoft.Extensions.Logging;

namespace ArmoniK.MonteCarlo.Worker
{
    public struct Asset
{
    public string Name { get; set; }
    public double Spot { get; set; }
    public double Volatility { get; set; }
    public double Weight { get; set; }
}

public class BasketSimulator
{
    private readonly Random _random;
    
    public BasketSimulator()
    {
        _random = new Random();
    }

    private double GenerateNormalRandom()
    {
        // Box-Muller transform to generate a standard normal random variable
        double u1 = 1.0 - _random.NextDouble(); // Uniform(0,1] random doubles
        double u2 = 1.0 - _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public double SimulateBasketValue(
        List<Asset> basket,
        double riskFreeRate,
        double timeToMaturity,
        int numPaths)
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

                // Generate random normal variable
                double Z = GenerateNormalRandom();

                // Calculate final stock price using geometric Brownian motion
                double ST = S0 * Math.Exp((riskFreeRate - 0.5 * sigma * sigma) * timeToMaturity
                                        + sigma * Math.Sqrt(timeToMaturity) * Z);

                pathValue += weight * ST;
            }

            totalValue += pathValue;
        }

        // Discount the average value back to present
        return Math.Exp(-riskFreeRate * timeToMaturity) * (totalValue / numPaths);
    }
}

  public class MonteCarloWorker : WorkerStreamWrapper
  {
    /// <summary>
    ///   Initializes an instance of <see cref="MonteCarloWorker" />
    /// </summary>
    /// <param name="loggerFactory">Factory to create loggers</param>
    /// <param name="computePlane">Compute Plane</param>
    /// <param name="provider">gRPC channel provider to send tasks and results to ArmoniK Scheduler</param>
    public MonteCarloWorker(ILoggerFactory      loggerFactory,
                            ComputePlane        computePlane,
                            GrpcChannelProvider provider)
      : base(loggerFactory,
             computePlane,
             provider)
      => logger_ = loggerFactory.CreateLogger<MonteCarloWorker>();

    /// <summary>
    ///   Function that represents the processing of a task.
    /// </summary>
    /// <param name="taskHandler">Handler that holds the payload, the task metadata and helpers to submit tasks and results</param>
    /// <returns>
    ///   An <see cref="Output" /> representing the status of the current task. This is the final step of the task.
    /// </returns>
    public override async Task<Output> Process(ITaskHandler taskHandler)
    {
      // Logger scope that will add metadata (session and task ids) for each use of the logger
      // It will facilitate the search for information related to the execution of the task/session
      using var scopedLog = logger_.BeginNamedScope("Execute task",
                                                    ("sessionId", taskHandler.SessionId),
                                                    ("taskId", taskHandler.TaskId));

      try
      {
        // We convert the binary payload from the handler back to the string sent by the client
        var input = Encoding.ASCII.GetString(taskHandler.Payload);
        ExtractValues(input, out int numSimulations, out double riskFreeRate, out double timeToMaturity);
        List<Asset> basket = new List<Asset>
        {
            new Asset { Name = "AAPL", Spot = 180.0, Volatility = 0.25, Weight = 0.4 },
            new Asset { Name = "MSFT", Spot = 350.0, Volatility = 0.20, Weight = 0.3 },
            new Asset { Name = "GOOGL", Spot = 140.0, Volatility = 0.28, Weight = 0.3 }
        };
        BasketSimulator simulator = new BasketSimulator();
        double value = simulator.SimulateBasketValue(
            basket,
            riskFreeRate,
            timeToMaturity,
            numSimulations
        );

        // We get the result that the task should produce
        // The handler has this information
        // It also contains other information such as the data dependencies (id and binary data) if any
        var resultId = taskHandler.ExpectedResults.Single();
        // We the result of the task using through the handler
        await taskHandler.SendResult(resultId,
                                     Encoding.ASCII.GetBytes($"{value}"))
                         .ConfigureAwait(false);
      }
      // If there is an exception, we put the task in error
      // The task will not be retried by ArmoniK
      // An uncatched exception means that the task will be retried
      catch (Exception e)
      {
        logger_.LogError(e,
                         "Error during task computing.");
        return new Output
               {
                 Error = new Output.Types.Error
                         {
                           Details = e.Message,
                         },
               };
      }

      // Return an OK output
      // The task finished successfully
      return new Output
             {
               Ok = new Empty(),
             };
    }

    static void ExtractValues(string input, out int numSimulations, out double riskFreeRate, out double timeToMaturity)
    {
        Regex regex = new Regex(@"numSimulations:\s*(\d+),\s*riskFreeRate:\s*([\d\.]+),\s*timeToMaturity:\s*([\d\.]+)");
        Match match = regex.Match(input);

        if (!match.Success)
        {
            throw new FormatException("Input string format is incorrect. Expected format: 'numSimulations: <int>, riskFreeRate: <float>, timeToMaturity: <float>'");
        }

        if (!int.TryParse(match.Groups[1].Value, out numSimulations) || !double.TryParse(match.Groups[2].Value, out riskFreeRate) || !double.TryParse(match.Groups[3].Value, out timeToMaturity))
        {
            throw new FormatException("Failed to parse values as integer and doubles.");
        }
    }
  }
}
