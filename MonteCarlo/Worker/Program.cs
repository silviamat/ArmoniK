using ArmoniK.Api.Worker.Utils;
using ArmoniK.MonteCarlo.Worker;

// Create the application and register the hello world example worker implementation
WorkerServer.Create<MonteCarloWorker>()
            .Run();