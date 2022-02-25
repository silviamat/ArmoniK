# Kubernetes namespace
namespace = "armonik"

# Monitoring infos
monitoring = {
  seq           = {
    enabled            = true
    image              = "datalust/seq"
    tag                = "2021.4"
    image_pull_secrets = ""
    service_type       = "LoadBalancer"
    node_selector      = {}
  }
  grafana       = {
    enabled            = true
    image              = "grafana/grafana"
    tag                = "latest"
    image_pull_secrets = ""
    service_type       = "LoadBalancer"
    node_selector      = {}
  }
  node_exporter = {
    enabled            = true
    image              = "prom/node-exporter"
    tag                = "latest"
    image_pull_secrets = ""
    node_selector      = {}
  }
  prometheus    = {
    enabled            = true
    image              = "prom/prometheus"
    tag                = "latest"
    image_pull_secrets = ""
    service_type       = "ClusterIP"
    node_selector      = {}
  }
  fluent_bit    = {
    image              = "fluent/fluent-bit"
    tag                = "1.7.2"
    image_pull_secrets = ""
    is_daemonset       = false
    http_port          = 2020 # 0 or 2020
    read_from_head     = true
    node_selector      = {}
  }
}
