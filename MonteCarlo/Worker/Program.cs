using ArmoniK.Api.Worker.Utils;
using ArmoniK.Samples.HelloWorld.Worker;

// Create the application and register the hello world example worker implementation
WorkerServer.Create<HelloWorldWorker>()
            .Run();