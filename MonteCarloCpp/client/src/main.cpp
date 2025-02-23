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

namespace ak_common = armonik::api::common;
namespace ak_client = armonik::api::client;
namespace ak_grpc = armonik::api::grpc::v1;

int main() {
    // Initialize logger and configuration
    ak_common::logger::Logger logger{ak_common::logger::writer_console(), ak_common::logger::formatter_plain(true)};
    ak_common::utils::Configuration config;
    config.add_json_configuration("/appsettings.json").add_env_configuration();
    logger.info("Initialized client config.");

    // Setup channel and clients
    ak_client::ChannelFactory channelFactory(config, logger);
    std::shared_ptr<::grpc::Channel> channel = channelFactory.create_channel();
    
    // Configure task options
    ak_grpc::TaskOptions taskOptions;
    std::string used_partition = "default";
    taskOptions.mutable_max_duration()->set_seconds(3600);
    taskOptions.set_max_retries(3);
    taskOptions.set_priority(1);
    taskOptions.set_partition_id(used_partition);
    taskOptions.set_application_name("basket-valuation");
    taskOptions.set_application_version("1.0");
    taskOptions.set_application_namespace("finance");

    // Initialize clients
    ak_client::TasksClient tasksClient(ak_grpc::tasks::Tasks::NewStub(channel));
    ak_client::ResultsClient resultsClient(ak_grpc::results::Results::NewStub(channel));
    ak_client::SessionsClient sessionsClient(ak_grpc::sessions::Sessions::NewStub(channel));
    ak_client::EventsClient eventsClient(ak_grpc::events::Events::NewStub(channel));

    // Create payload string
    std::stringstream ss;
    
    // First line: simulation parameters (risk_free_rate, time_to_maturity, num_simulations)
    ss << "0.05,1.0,10000\n";
    
    // Following lines: assets (name, spot, volatility, weight)
    ss << "AAPL,180.0,0.25,0.4\n";
    ss << "MSFT,350.0,0.20,0.3\n";
    ss << "GOOGL,140.0,0.28,0.3";

    std::string payload = ss.str();

    // Create session
    std::string session_id = sessionsClient.create_session(taskOptions, {used_partition});
    logger.info("Created session with id = " + session_id);

    // Create results
    std::map<std::string, std::string> results = resultsClient.create_results_metadata(session_id, {"output", "payload"});

    // Upload payload
    resultsClient.upload_result_data(session_id, results["payload"], payload);
    logger.info("Uploaded payload.");

    // Submit task
    auto task_info = tasksClient.submit_tasks(session_id, {ak_common::TaskCreation{results["payload"], {results["output"]}}})[0];
    logger.info("Task submitted.");

    // Wait for and process result
    logger.info("Waiting for result with id = " + results["output"]);
    eventsClient.wait_for_result_availability(session_id, {results["output"]});
    logger.info("Finished waiting.");

    // Get result
    std::string result = resultsClient.download_result_data(session_id, {results["output"]});
    double basket_value = std::stod(result);

    logger.info("Basket value = " + std::to_string(basket_value));
    return 0;
}