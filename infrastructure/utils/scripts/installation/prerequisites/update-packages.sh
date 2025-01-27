#! /bin/sh
# This script is used to update packages.

sudo apt update --allow-insecure-repositories && sudo apt -y upgrade
