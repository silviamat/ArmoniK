#include "objects.pb.h"
#include "utils/Configuration.h"
#include "logger/logger.h"
#include "logger/writer.h"
#include "logger/formatter.h"
#include "channel/ChannelFactory.h"
#include "sessions/SessionsClient.h"
#include "tasks/TasksClient.h"
#include "results/ResultsClient.h" 
#include "events/EventsClient.h" 
#include <map>
#include <vector>
#include <sstream>
#include <nlohmann/json.hpp>

using json = nlohmann::json;
namespace ak_common = armonik::api::common;
namespace ak_client = armonik::api::client;
namespace ak_grpc = armonik::api::grpc::v1;

struct Asset {
    std::string name;
    double spot;
    double volatility;
    double weight;
};

int main() {
    ak_common::logger::Logger logger{ak_common::logger::writer_console(), ak_common::logger::formatter_plain(true)};
    ak_common::utils::Configuration config;

    config.add_json_configuration("/appsettings.json").add_env_configuration();
    logger.info("Initialized client config.");
    
    // Define basket parameters
    std::vector<Asset> basket = {
        {"AAPL", 180.0, 0.25, 0.4},
        {"MSFT", 350.0, 0.20, 0.3},
        {"GOOGL", 140.0, 0.28, 0.3}
    };
    
    // Simulation parameters
    const int num_simulations = 10000;
    const int simulations_per_task = 1000;
    const double risk_free_rate = 0.05;
    const double time_to_maturity = 1.0;
    
    // Create channel and clients
    ak_client::ChannelFactory channelFactory(config, logger);
    std::shared_ptr<::grpc::Channel> channel = channelFactory.create_channel();

    ak_grpc::TaskOptions taskOptions;
    std::string used_partition = "default";
    
    taskOptions.mutable_max_duration()->set_seconds(3600);
    taskOptions.set_max_retries(3);
    taskOptions.set_priority(1);
    taskOptions.set_partition_id(used_partition);
    taskOptions.set_application_name("monte-carlo-cpp");
    taskOptions.set_application_version("1.0");
    taskOptions.set_application_namespace("samples");

    ak_client::TasksClient tasksClient(ak_grpc::tasks::Tasks::NewStub(channel));
    ak_client::ResultsClient resultsClient(ak_grpc::results::Results::NewStub(channel));
    ak_client::SessionsClient sessionsClient(ak_grpc::sessions::Sessions::NewStub(channel));
    ak_client::EventsClient eventsClient(ak_grpc::events::Events::NewStub(channel));

    // Create session
    std::string session_id = sessionsClient.create_session(taskOptions, {used_partition});
    logger.info("Created session with id = " + session_id);

    // Calculate number of tasks needed
    const int num_tasks = (num_simulations + simulations_per_task - 1) / simulations_per_task;
    std::vector<std::string> output_results;
    std::vector<std::string> payloads;

    // Prepare tasks
    for (int i = 0; i < num_tasks; i++) {
        // Create JSON payload
        json payload = {
            {"basket", json::array()},
            {"risk_free_rate", risk_free_rate},
            {"time_to_maturity", time_to_maturity},
            {"num_simulations", simulations_per_task}
        };

        for (const auto& asset : basket) {
            payload["basket"].push_back({
                {"name", asset.name},
                {"spot", asset.spot},
                {"volatility", asset.volatility},
                {"weight", asset.weight}
            });
        }

        std::map<std::string,std::string> results = resultsClient.create_results_metadata(session_id, {"output" + std::to_string(i), "payload" + std::to_string(i)});
        resultsClient.upload_result_data(session_id, results["payload" + std::to_string(i)], payload.dump());
        output_results.push_back(results["output" + std::to_string(i)]);
        payloads.push_back(results["payload" + std::to_string(i)]);
    }

    // Submit tasks
    std::vector<ak_common::TaskCreation> tasks;
    for (size_t i = 0; i < payloads.size(); i++) {
        tasks.push_back(ak_common::TaskCreation{payloads[i], {output_results[i]}});
    }
    auto task_infos = tasksClient.submit_tasks(session_id, tasks);
    logger.info("Submitted " + std::to_string(num_tasks) + " tasks");

    // Wait for all results
    eventsClient.wait_for_result_availability(session_id, output_results);
    logger.info("All tasks completed");

    // Aggregate results
    double total_value = 0.0;
    int total_sims = 0;
    
    for (const auto& result_id : output_results) {
        std::string result_str = resultsClient.download_result_data(session_id, {result_id});
        double task_result = std::stod(result_str);
        total_value += task_result;
        total_sims += simulations_per_task;
    }

    double basket_value = total_value / num_tasks;
    logger.info("Final basket value = " + std::to_string(basket_value));

    return 0;
}