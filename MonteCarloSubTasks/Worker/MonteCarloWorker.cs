using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using ArmoniK.Api.Common.Channel.Utils;
using ArmoniK.Api.Common.Options;
using ArmoniK.Api.Common.Utils;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.gRPC.V1.Agent;
using ArmoniK.Api.Worker.Worker;

using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

using Microsoft.Extensions.Logging;

using Empty = ArmoniK.Api.gRPC.V1.Empty;

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
        double timeToMaturity)
    {
        double totalValue = 0.0;

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

            totalValue += weight * ST;
        }

        // Discount the average value back to present
        return Math.Exp(-riskFreeRate * timeToMaturity) * totalValue;
    }
}
  public class MonteCarloWorker : WorkerStreamWrapper
  {
    /// <summary>
    ///   Initializes an instance of <see cref="SubTaskingWorker" />
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
    /// <param name="cancellationToken"></param>
    /// <returns>
    ///   An <see cref="Output" /> representing the status of the current task. This is the final step of the task.
    /// </returns>
    public override async Task<Output> ProcessAsync(ITaskHandler      taskHandler,
                                                    CancellationToken cancellationToken)
    {
      using var scopedLog = logger_.BeginNamedScope("Execute task",
                                                    ("sessionId", taskHandler.SessionId),
                                                    ("taskId", taskHandler.TaskId));
      try
      {
        // We may use TaskOptions.Options to send a field UseCase where we inform
        // what should be executed
        var useCase = taskHandler.TaskOptions.Options["UseCase"];

        switch (useCase)
        {
          case "Launch":
            var resultIds = await SubmitWorkers(taskHandler);
            await SubmitJoiner(taskHandler,
                               resultIds);
            break;
          case "Joiner":
            await Joiner(taskHandler);
            break;
          case "MonteCarloWorker":
            await Worker(taskHandler);
            break;
          default:
            return new Output
                   {
                     Error = new Output.Types.Error
                             {
                               Details = "UseCase not found",
                             },
                   };
        }
      }
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

      return new Output
             {
               Ok = new Empty(),
             };
    }

    private async Task<List<string>> SubmitWorkers(ITaskHandler taskHandler)
    {
      logger_.LogDebug("Submitting Workers");

      var input = Encoding.ASCII.GetString(taskHandler.Payload);

      var taskOptions = new TaskOptions
                        {
                          MaxDuration = Duration.FromTimeSpan(TimeSpan.FromHours(1)),
                          MaxRetries  = 2,
                          Priority    = 1,
                          PartitionId = taskHandler.TaskOptions.PartitionId,
                          Options =
                          {
                            new MapField<string, string>
                            {
                              {
                                "UseCase", "MonteCarloWorker"
                              },
                            },
                          },
                        };

      var subTaskResults = await taskHandler.CreateResultsMetaDataAsync(Enumerable.Range(1,
                                                                                         100)
                                                                                  .Select(i => new CreateResultsMetaDataRequest.Types.ResultCreate
                                                                                               {
                                                                                                 Name = Guid.NewGuid() + "_" + i,
                                                                                               })
                                                                                  .ToList());

      var subTasksResultIds = subTaskResults.Results.Select(result => result.ResultId)
                                            .ToList();

      var payload = await taskHandler.CreateResultsAsync(new List<CreateResultsRequest.Types.ResultCreate>
                                                         {
                                                           new()
                                                           {
                                                             Data = UnsafeByteOperations.UnsafeWrap(Encoding.ASCII.GetBytes($"{input}")),
                                                             Name = "Payload",
                                                           },
                                                         });

      var payloadId = payload.Results.Single()
                             .ResultId;

      await taskHandler.SubmitTasksAsync(new List<SubmitTasksRequest.Types.TaskCreation>(subTasksResultIds.Select(subTaskId => new SubmitTasksRequest.Types.TaskCreation
                                                                                                                               {
                                                                                                                                 PayloadId = payloadId,
                                                                                                                                 ExpectedOutputKeys =
                                                                                                                                 {
                                                                                                                                   subTaskId,
                                                                                                                                 },
                                                                                                                               })
                                                                                                          .ToList()),
                                         taskOptions);

      return subTasksResultIds;
    }

    private async Task SubmitJoiner(ITaskHandler        taskHandler,
                                    IEnumerable<string> expectedOutputIds)
    {
      logger_.LogDebug("Submitting Joiner");
      var taskOptions = new TaskOptions
                        {
                          MaxDuration = Duration.FromTimeSpan(TimeSpan.FromHours(1)),
                          MaxRetries  = 2,
                          Priority    = 1,
                          PartitionId = taskHandler.TaskOptions.PartitionId,
                          Options =
                          {
                            new MapField<string, string>
                            {
                              {
                                "UseCase", "Joiner"
                              },
                            },
                          },
                        };

      var subTaskResultId = taskHandler.ExpectedResults.Single();

      var payload = await taskHandler.CreateResultsAsync(new List<CreateResultsRequest.Types.ResultCreate>
                                                         {
                                                           new()
                                                           {
                                                             Data = UnsafeByteOperations.UnsafeWrap(taskHandler.Payload),
                                                             Name = "Payload",
                                                           },
                                                         });

      var payloadId = payload.Results.Single()
                             .ResultId;

      await taskHandler.SubmitTasksAsync(new List<SubmitTasksRequest.Types.TaskCreation>
                                         {
                                           new()
                                           {
                                             PayloadId = payloadId,
                                             ExpectedOutputKeys =
                                             {
                                               subTaskResultId,
                                             },
                                             DataDependencies =
                                             {
                                               expectedOutputIds,
                                             },
                                           },
                                         },
                                         taskOptions);
    }

    private static async Task Worker(ITaskHandler taskHandler)
    {
        var input = Encoding.ASCII.GetString(taskHandler.Payload);
        ExtractValues(input, out double riskFreeRate, out double timeToMaturity);
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
            timeToMaturity
        );

      var resultId = taskHandler.ExpectedResults.Single();
      // We add the SubTaskId to the result 
      await taskHandler.SendResult(resultId,
                                   Encoding.ASCII.GetBytes($"{value}"))
                       .ConfigureAwait(false);
    }

    private async Task Joiner(ITaskHandler taskHandler)
    {
        logger_.LogDebug("Starting Joiner useCase");
        var resultId = taskHandler.ExpectedResults.Single();

        // Get results as strings from dependencies
        var resultsArray = taskHandler.DataDependencies.Values
                                    .Select(dependency => Encoding.ASCII.GetString(dependency))
                                    .Select(result => $"{result}")
                                    .ToList();

        // Serialize the list as a proper vector/array
        var serializedVector = System.Text.Json.JsonSerializer.Serialize(resultsArray);

        // Send the serialized vector as the result
        await taskHandler.SendResult(resultId, 
                                Encoding.UTF8.GetBytes(serializedVector));
    }

    static void ExtractValues(string input, out double riskFreeRate, out double timeToMaturity)
    {
        Regex regex = new Regex(@"riskFreeRate:\s*([\d\.]+),\s*timeToMaturity:\s*([\d\.]+)");
        Match match = regex.Match(input);

        if (!match.Success)
        {
            throw new FormatException("Input string format is incorrect. Expected format: 'riskFreeRate: <float>, timeToMaturity: <float>'");
        }

        if (!double.TryParse(match.Groups[1].Value, out riskFreeRate) || !double.TryParse(match.Groups[2].Value, out timeToMaturity))
        {
            throw new FormatException("Failed to parse values as doubles.");
        }
    }
  }
}
