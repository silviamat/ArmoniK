# Profile
profile = "default"

# Region
region = "eu-west-3"

# Kubeconfig path
k8s_config_path = "~/.kube/config"

# Kubeconfig context
k8s_config_context = "default"

# Kubernetes namespace
namespace = "armonik"

# Logging level
logging_level = "Information"

# Polling delay to MongoDB
# according to the size of the task and/or the application
mongodb_polling_delay = {
  min_polling_delay = "00:00:01"
  max_polling_delay = "00:00:10"
}

# Parameters of control plane
control_plane = {
  name               = "control-plane"
  service_type       = "ClusterIP"
  replicas           = 1
  image              = "125796369274.dkr.ecr.eu-west-3.amazonaws.com/armonik-control-plane"
  tag                = "0.5.6"
  image_pull_policy  = "IfNotPresent"
  port               = 5001
  limits             = {
    cpu    = "1000m"
    memory = "2048Mi"
  }
  requests           = {
    cpu    = "200m"
    memory = "256Mi"
  }
  image_pull_secrets = ""
  node_selector      = {}
  annotations        = {}
}

# Parameters of the compute plane
compute_plane = [
  {
    name                             = "compute-plane"
    # number of replicas for each deployment of compute plane
    replicas                         = 1
    termination_grace_period_seconds = 30
    image_pull_secrets               = ""
    node_selector                    = {}
    # ArmoniK polling agent
    polling_agent                    = {
      image             = "125796369274.dkr.ecr.eu-west-3.amazonaws.com/armonik-polling-agent"
      tag               = "0.5.6"
      image_pull_policy = "IfNotPresent"
      limits            = {
        cpu    = "1000m"
        memory = "2048Mi"
      }
      requests          = {
        cpu    = "200m"
        memory = "256Mi"
      }
    }
    # ArmoniK workers
    worker                           = [
      {
        name              = "worker"
        port              = 80
        image             = "125796369274.dkr.ecr.eu-west-3.amazonaws.com/armonik-worker"
        tag               = "0.5.3"
        image_pull_policy = "IfNotPresent"
        limits            = {
          cpu    = "1000m"
          memory = "1024Mi"
        }
        requests          = {
          cpu    = "500m"
          memory = "512Mi"
        }
      }
    ]
    hpa                              = {
      min_replicas   = 1
      max_replicas   = 100
      object_metrics = [
        {
          described_object = {
            api_version = "batch/v1"
            kind        = "Job"
          }
          metric_name      = "armonik_tasks_queued"
          target           = {
            type                = "AverageValue" # "Value", "Utilization" or "AverageValue"
            average_value       = 2
            average_utilization = 0
            value               = 0
          }
        }
      ]
    }
  }
]

ingress = {
  name               = "ingress"
  service_type       = "LoadBalancer"
  replicas           = 1
  image              = "125796369274.dkr.ecr.eu-west-3.amazonaws.com/nginx"
  tag                = "latest"
  image_pull_policy  = "IfNotPresent"
  http_port          = 8443
  grpc_port          = 5001
  limits             = {
    cpu    = "200m"
    memory = "100Mi"
  }
  requests           = {
    cpu    = "1m"
    memory = "1Mi"
  }
  image_pull_secrets = ""
  node_selector      = {}
  tls                = false
  mtls               = false
}
