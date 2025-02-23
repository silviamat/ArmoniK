using ArmoniK.Api.Worker.Utils;
using ArmoniK.Samples.MonteCarloSimulation.Worker;

WorkerServer.Create<MonteCarloWorker>()
           .Run();