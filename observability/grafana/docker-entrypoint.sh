#!/bin/sh
# Provisions Loki ONLY where the deployment names one, and Tempo likewise, then hands
# over to Grafana's own /run.sh.
#
# WHY. An environment without Loki must show no Loki datasource and no Loki panel - not a panel
# that errors, and not one that renders empty as though there were no logs. A red panel in the one
# environment that will never have Loki trains operators to ignore red panels. So an unset or empty
# LOKI_URL removes the Loki datasource file and swaps in the dashboard variant built without its
# Loki panel (see the Dockerfile's `dashboards` stage). The image sets NO default for LOKI_URL, so
# forgetting it reads as "no Loki", never as a URL pointing at nothing.
#
# FAIL-SOFT, DELIBERATELY: a prune that cannot write (a read-only filesystem, another uid) logs a
# warning and Grafana starts anyway, because a red Loki panel is a lesser failure than no dashboard.
# The dashboard is swapped first and the datasource removed only after that worked, so a failure
# leaves either everything as built or the panel gone - never a Loki panel with no datasource.
#
# DEPLOYMENT.md
set -u

if [ -z "${LOKI_URL:-}" ]; then
    if ! { cp /usr/share/agentforge/agentforge.metrics-only.json /var/lib/grafana/dashboards/agentforge.json \
           && rm -f /etc/grafana/provisioning/datasources/loki.yml; }; then
        echo "agentforge-entrypoint: WARNING - LOKI_URL is unset but Loki could not be un-provisioned;" \
             "starting with the Loki datasource/panel as built" >&2
    fi
fi

# Tempo, the same rule with less to do: no dashboard panel reads Tempo, so an unset or empty
# TEMPO_URL only removes its datasource. Fail-soft for the same reason as above.
if [ -z "${TEMPO_URL:-}" ]; then
    if ! rm -f /etc/grafana/provisioning/datasources/tempo.yml; then
        echo "agentforge-entrypoint: WARNING - TEMPO_URL is unset but Tempo could not be un-provisioned;" \
             "starting with the Tempo datasource as built" >&2
    fi
fi

exec /run.sh "$@"
