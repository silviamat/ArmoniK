using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ArmoniK.Api.Common.Channel.Utils;
using ArmoniK.Api.Common.Options;
using ArmoniK.Api.Common.Utils;
using ArmoniK.Api.gRPC.V1;
using ArmoniK.Api.Worker.Worker;

using Microsoft.Extensions.Logging;

namespace ArmoniK.MonteCarlo.Worker
{
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

        // We get the result that the task should produce
        // The handler has this information
        // It also contains other information such as the data dependencies (id and binary data) if any
        var resultId = taskHandler.ExpectedResults.Single();
        // We the result of the task using through the handler
        await taskHandler.SendResult(resultId,
                                     Encoding.ASCII.GetBytes($"{input} World_ {resultId}"))
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
  }
}
