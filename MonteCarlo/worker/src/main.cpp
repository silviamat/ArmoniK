#include <iostream>
#include <memory>
#include <vector>
#include <random>
#include <cmath>
#include <string>
#include <sstream>
#include <grpcpp/grpcpp.h>
#include "grpcpp/support/sync_stream.h"
#include "objects.pb.h"
#include "utils/WorkerServer.h"
#include "Worker/ArmoniKWorker.h"
#include "Worker/ProcessStatus.h"
#include "Worker/TaskHandler.h"
#include "exceptions/ArmoniKApiException.h"

struct Asset {
    std::string name;
    double spot;
    double volatility;
    double weight;
};

// Helper function to split string by delimiter
std::vector<std::string> split(const std::string& str, char delim) {
    std::vector<std::string> tokens;
    std::stringstream ss(str);
    std::string token;
    while (std::getline(ss, token, delim)) {
        tokens.push_back(token);
    }
    return tokens;
}

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
        double total_value = 0.0;

        for (int path = 0; path < num_paths; path++) {
            double path_value = 0.0;

            for (const auto& asset : basket) {
                double S0 = asset.spot;
                double sigma = asset.volatility;
                double weight = asset.weight;
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
            std::vector<std::string> lines = split(payload, '\n');
            
            if (lines.size() < 2) {
                throw std::runtime_error("Invalid payload format");
            }

            // Parse simulation parameters from first line
            std::vector<std::string> params = split(lines[0], ',');
            if (params.size() != 3) {
                throw std::runtime_error("Invalid parameters format");
            }

            double risk_free_rate = std::stod(params[0]);
            double time_to_maturity = std::stod(params[1]);
            int num_simulations = std::stoi(params[2]);

            // Parse assets
            std::vector<Asset> assets;
            for (size_t i = 1; i < lines.size(); i++) {
                std::vector<std::string> asset_params = split(lines[i], ',');
                if (asset_params.size() != 4) {
                    throw std::runtime_error("Invalid asset format at line " + std::to_string(i));
                }
                
                assets.push_back({
                    asset_params[0],
                    std::stod(asset_params[1]),
                    std::stod(asset_params[2]),
                    std::stod(asset_params[3])
                });
            }

            // Run simulation
            BasketSimulator simulator;
            double result = simulator.simulate_basket_value(
                assets,
                risk_free_rate,
                time_to_maturity,
                num_simulations
            );

            // Convert result to string
            std::string result_str = std::to_string(result);

            // Send result
            if (!taskHandler.getExpectedResults().empty()) {
                taskHandler.send_result(taskHandler.getExpectedResults()[0], result_str).get();
            }

            return armonik::api::worker::ProcessStatus::Ok;
        } catch (const std::exception &e) {
            std::cout << "Error in worker execution: " << e.what() << std::endl;
            return armonik::api::worker::ProcessStatus(e.what());
        }
    }
};

int main() {
    std::cout << "Basket Valuation Worker started. gRPC version = " << grpc::Version() << "\n";
    
    armonik::api::common::utils::Configuration config;
    config.add_json_configuration("/appsettings.json").add_env_configuration();
    config.set("ComputePlane__WorkerChannel__Address", "/cache/armonik_worker.sock");
    config.set("ComputePlane__AgentChannel__Address", "/cache/armonik_agent.sock");

    try {
        armonik::api::worker::WorkerServer::create<BasketWorker>(config)->run();
    } catch (const std::exception &e) {
        std::cout << "Error in worker: " << e.what() << std::endl;
    }

    std::cout << "Stopping Server..." << std::endl;
    return 0;
}