#include <iostream>
#include <memory>
#include <vector>
#include <random>
#include <cmath>
#include <string>
#include <sstream>
#include <stdexcept>
#include <grpcpp/grpcpp.h>
#include "grpcpp/support/sync_stream.h"
#include "objects.pb.h"
#include "utils/WorkerServer.h"
#include "Worker/ArmoniKWorker.h"
#include "Worker/ProcessStatus.h"
#include "Worker/TaskHandler.h"
#include "exceptions/ArmoniKApiException.h"

// Helper function for logging
void log_message(const std::string& prefix, const std::string& message) {
    std::cout << "[" << prefix << "] " << message << std::endl;
}

// Helper function to split string by delimiter with error checking
std::vector<std::string> split(const std::string& str, char delim) {
    std::vector<std::string> tokens;
    std::stringstream ss(str);
    std::string token;
    while (std::getline(ss, token, delim)) {
        if (!token.empty()) {  // Skip empty tokens
            tokens.push_back(token);
        }
    }
    return tokens;
}

struct Asset {
    std::string name;
    double spot;
    double volatility;
    double weight;
};

class BasketSimulator {
private:
    std::mt19937 gen;
    std::normal_distribution<> normal;

public:
    BasketSimulator() :
        gen(std::random_device()()),
        normal(0.0, 1.0) {}

    double simulate_basket_value(
        const std::vector<Asset>& basket,
        double risk_free_rate,
        double time_to_maturity,
        int num_paths
    ) {
        if (basket.empty()) {
            throw std::invalid_argument("Asset basket cannot be empty");
        }
        if (num_paths <= 0) {
            throw std::invalid_argument("Number of paths must be positive");
        }
        if (time_to_maturity <= 0) {
            throw std::invalid_argument("Time to maturity must be positive");
        }

        double total_value = 0.0;
        double total_weight = 0.0;

        // Validate weights
        for (const auto& asset : basket) {
            if (asset.weight < 0) {
                throw std::invalid_argument("Asset weights cannot be negative");
            }
            total_weight += asset.weight;
        }
        if (std::abs(total_weight - 1.0) > 1e-6) {
            throw std::invalid_argument("Asset weights must sum to 1");
        }

        for (int path = 0; path < num_paths; path++) {
            double path_value = 0.0;

            for (const auto& asset : basket) {
                double S0 = asset.spot;
                double sigma = asset.volatility;
                double weight = asset.weight;
                
                if (S0 <= 0) {
                    throw std::invalid_argument("Spot price must be positive");
                }
                if (sigma <= 0) {
                    throw std::invalid_argument("Volatility must be positive");
                }

                double Z = normal(gen);
                double ST = S0 * std::exp((risk_free_rate - 0.5 * sigma * sigma) * time_to_maturity
                                        + sigma * std::sqrt(time_to_maturity) * Z);
                path_value += weight * ST;
            }
            total_value += path_value;
        }
        return std::exp(-risk_free_rate * time_to_maturity) * (total_value / num_paths);
    }
};

class BasketWorker : public armonik::api::worker::ArmoniKWorker {
public:
    explicit BasketWorker(std::unique_ptr<armonik::api::grpc::v1::agent::Agent::Stub> agent)
        : ArmoniKWorker(std::move(agent)) {}

    armonik::api::worker::ProcessStatus Execute(armonik::api::worker::TaskHandler &taskHandler) override {
        try {
            std::string payload = taskHandler.getPayload();
            log_message("INFO", "Received payload: " + payload);

            if (payload.empty()) {
                throw std::runtime_error("Empty payload received");
            }

            std::vector<std::string> lines = split(payload, '\n');
            log_message("INFO", "Number of lines in payload: " + std::to_string(lines.size()));
            
            if (lines.size() < 2) {
                throw std::runtime_error("Invalid payload format: expected at least 2 lines, got " + 
                                       std::to_string(lines.size()));
            }

            // Parse simulation parameters from first line
            std::vector<std::string> params = split(lines[0], ',');
            if (params.size() != 3) {
                throw std::runtime_error("Invalid parameters format: expected 3 values, got " + 
                                       std::to_string(params.size()));
            }

            double risk_free_rate = std::stod(params[0]);
            double time_to_maturity = std::stod(params[1]);
            int num_simulations = std::stoi(params[2]);

            log_message("INFO", "Parsed parameters: risk_free_rate=" + std::to_string(risk_free_rate) + 
                               ", time_to_maturity=" + std::to_string(time_to_maturity) + 
                               ", num_simulations=" + std::to_string(num_simulations));

            // Parse assets
            std::vector<Asset> assets;
            for (size_t i = 1; i < lines.size(); i++) {
                std::vector<std::string> asset_params = split(lines[i], ',');
                if (asset_params.size() != 4) {
                    throw std::runtime_error("Invalid asset format at line " + std::to_string(i) + 
                                           ": expected 4 values, got " + std::to_string(asset_params.size()));
                }
                
                Asset asset{
                    asset_params[0],
                    std::stod(asset_params[1]),
                    std::stod(asset_params[2]),
                    std::stod(asset_params[3])
                };

                log_message("INFO", "Parsed asset: name=" + asset.name + 
                                   ", spot=" + std::to_string(asset.spot) + 
                                   ", vol=" + std::to_string(asset.volatility) + 
                                   ", weight=" + std::to_string(asset.weight));

                assets.push_back(asset);
            }

            // Run simulation
            log_message("INFO", "Starting simulation...");
            BasketSimulator simulator;
            double result = simulator.simulate_basket_value(
                assets,
                risk_free_rate,
                time_to_maturity,
                num_simulations
            );
            log_message("INFO", "Simulation completed. Result: " + std::to_string(result));

            // Send result
            if (taskHandler.getExpectedResults().empty()) {
                throw std::runtime_error("No expected results defined");
            }

            std::string result_str = std::to_string(result);
            log_message("INFO", "Sending result: " + result_str);
            
            taskHandler.send_result(taskHandler.getExpectedResults()[0], result_str).get();
            log_message("INFO", "Result sent successfully");

            return armonik::api::worker::ProcessStatus::Ok;
        } catch (const std::exception &e) {
            std::string error_msg = "Error in worker execution: " + std::string(e.what());
            log_message("ERROR", error_msg);
            return armonik::api::worker::ProcessStatus(error_msg);
        }
    }
};

int main() {
    try {
        log_message("INFO", "Basket Valuation Worker started. gRPC version = " + std::string(grpc::Version()));
        
        armonik::api::common::utils::Configuration config;
        config.add_json_configuration("/appsettings.json").add_env_configuration();
        config.set("ComputePlane__WorkerChannel__Address", "/cache/armonik_worker.sock");
        config.set("ComputePlane__AgentChannel__Address", "/cache/armonik_agent.sock");

        armonik::api::worker::WorkerServer::create<BasketWorker>(config)->run();
    } catch (const std::exception &e) {
        log_message("ERROR", "Fatal error in worker: " + std::string(e.what()));
        return 1;
    }

    log_message("INFO", "Stopping Server...");
    return 0;
}