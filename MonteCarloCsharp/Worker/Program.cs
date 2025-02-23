using ArmoniK.Api.Worker.Utils;
using MonteCarlo.Worker;

// Create the application and register the hello world example worker implementation
WorkerServer.Create<BasketSimulationWorker>()
            .Run();
