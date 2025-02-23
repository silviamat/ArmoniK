#include <iostream>
#include <memory>
#include <random>
#include <vector>
#include <cmath>
#include <sstream>
#include <string>
#include <map>

#include <grpcpp/grpcpp.h>
#include "grpcpp/support/sync_stream.h"
#include "objects.pb.h"
#include "utils/WorkerServer.h"
#include "Worker/ArmoniKWorker.h"
#include "Worker/ProcessStatus.h"
#include "Worker/TaskHandler.h"
#include "exceptions/ArmoniKApiException.h"

// Custom structures to replace JSON
struct Asset {
    std::string name;
    double spot;
    double volatility;
    double weight;

    std::string serialize() const {
        std::stringstream ss;
        ss << name << "," << spot << "," << volatility << "," << weight;
        return ss.str();
    }

    static Asset deserialize(const std::string& str) {
        std::stringstream ss(str);
        std::string name;
        double spot, volatility, weight;
        
        std::getline(ss, name, ',');
        ss >> spot;
        ss.ignore(); // skip comma
        ss >> volatility;
        ss.ignore();
        ss >> weight;
        
        return Asset{name, spot, volatility, weight};
    }
};

struct SimulationParams {
    std::vector<Asset> basket;
    double risk_free_rate;
    double time_to_maturity;
    int num_simulations;

    std::string serialize() const {
        std::stringstream ss;
        ss << risk_free_rate << "\n";
        ss << time_to_maturity << "\n";
        ss << num_simulations << "\n";
        ss << basket.size() << "\n";
        
        for (const auto& asset : basket) {
            ss << asset.serialize() << "\n";
        }
        return ss.str();
    }

    static SimulationParams deserialize(const std::string& str) {
        std::stringstream ss(str);
        SimulationParams params;
        
        ss >> params.risk_free_rate;
        ss >> params.time_to_maturity;
        ss >> params.num_simulations;
        
        size_t basket_size;
        ss >> basket_size;
        ss.ignore(); // skip newline

        for (size_t i = 0; i < basket_size; i++) {
            std::string asset_str;
            std::getline(ss, asset_str);
            params.basket.push_back(Asset::deserialize(asset_str));
        }
        
        return params;
    }
};

class MonteCarloWorker: public armonik::api::worker::ArmoniKWorker {
public: 
    explicit MonteCarloWorker(std::unique_ptr<armonik::api::grpc::v1::agent::Agent::Stub> agent)
        : ArmoniKWorker(std::move(agent)) {}

    double simulate_basket_value(const std::vector<Asset>& basket, double risk_free_rate, 
                               double time_to_maturity, int num_paths) {
        std::random_device rd;
        std::mt19937 gen(rd());
        std::normal_distribution<> normal(0.0, 1.0);
        
        double total_value = 0.0;
        
        for (int path = 0; path < num_paths; path++) {
            double path_value = 0.0;
            
            for (const auto& asset : basket) {
                double S0 = asset.spot;
                double sigma = asset.volatility;
                double weight = asset.weight;
                
                // Generate random normal variable
                double Z = normal(gen);
                
                // Calculate final stock price using geometric Brownian motion
                double ST = S0 * std::exp((risk_free_rate - 0.5 * sigma * sigma) * time_to_maturity 
                                        + sigma * std::sqrt(time_to_maturity) * Z);
                
                path_value += weight * ST;
            }
            
            total_value += path_value;
        }
        
        // Discount the average value back to present
        return std::exp(-risk_free_rate * time_to_maturity) * (total_value / num_paths);
    }

    armonik::api::worker::ProcessStatus Execute(armonik::api::worker::TaskHandler &taskHandler) override {
        try {
            // Parse input parameters
            SimulationParams params = SimulationParams::deserialize(taskHandler.getPayload());
            
            // Run simulation
            double result = simulate_basket_value(params.basket, 
                                               params.risk_free_rate, 
                                               params.time_to_maturity, 
                                               params.num_simulations);
            
            // Send result
            if (!taskHandler.getExpectedResults().empty()) {
                taskHandler.send_result(taskHandler.getExpectedResults()[0], std::to_string(result)).get();
            }

            return armonik::api::worker::ProcessStatus::Ok;
        } catch (const std::exception &e) {
            std::cout << "Error in worker: " << e.what() << std::endl;
            return armonik::api::worker::ProcessStatus(e.what());
        }
    }
};

int main() {
    std::cout << "Monte Carlo Worker started. gRPC version = " << grpc::Version() << "\n";
    
    armonik::api::common::utils::Configuration config;
    config.add_json_configuration("/appsettings.json").add_env_configuration();

    config.set("ComputePlane__WorkerChannel__Address", "/cache/armonik_worker.sock");
    config.set("ComputePlane__AgentChannel__Address", "/cache/armonik_agent.sock");

    try {
        armonik::api::worker::WorkerServer::create<MonteCarloWorker>(config)->run();
    } catch (const std::exception &e) {
        std::cout << "Error in worker: " << e.what() << std::endl;
    }

    std::cout << "Stopping Server..." << std::endl;
    return 0;   
}