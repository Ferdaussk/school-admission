#!/bin/sh
# Writes the backend URL (API_URL env var) into config.js when the container starts.
echo "window.__API_URL__ = \"${API_URL:-http://localhost:8080}\";" > /usr/share/nginx/html/config.js
