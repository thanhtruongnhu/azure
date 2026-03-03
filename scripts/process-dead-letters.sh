#!/usr/bin/env bash
# Manually inspects and drains the dead-letter queue for a given subscription.
# Useful for: forensic analysis, manual resubmit, or emergency clearing.
#
# Usage:
#   ./scripts/process-dead-letters.sh                     # inspect only
#   ./scripts/process-dead-letters.sh --drain             # inspect + complete (discard)
#   ./scripts/process-dead-letters.sh --resubmit          # inspect + resubmit to topic

set -euo pipefail

MODE="${1:---inspect}"
NAMESPACE="${SERVICEBUS_NAMESPACE:-}"
TOPIC="reactor-events"

if [[ -z "$NAMESPACE" ]]; then
  echo "Set SERVICEBUS_NAMESPACE=<namespace>.servicebus.windows.net"
  exit 1
fi

echo "DLQ Inspector — Namespace: $NAMESPACE"
echo "Mode: $MODE"
echo ""

for SUBSCRIPTION in "safety-processor" "audit-logger"; do
  echo "=== Subscription: $SUBSCRIPTION ==="

  # Count DLQ messages
  DLQ_COUNT=$(az servicebus topic subscription show \
    --namespace-name "${NAMESPACE%.servicebus.windows.net}" \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --topic-name "$TOPIC" \
    --name "$SUBSCRIPTION" \
    --query "countDetails.deadLetterMessageCount" -o tsv 2>/dev/null || echo "0")

  echo "  Dead-letter count: $DLQ_COUNT"

  if [[ "$DLQ_COUNT" == "0" ]]; then
    echo "  DLQ is empty."
    continue
  fi

  echo "  To peek at DLQ messages, use the Azure Portal:"
  echo "  Service Bus → $NAMESPACE → Topics → $TOPIC → Subscriptions → $SUBSCRIPTION → Dead-letter"
  echo ""
  echo "  Or use the Azure CLI (requires Service Bus Data Receiver role):"
  echo "  az servicebus message peek --namespace-name ... --topic-name $TOPIC"
  echo "  --subscription-name $SUBSCRIPTION/\$deadletterqueue"
done

echo ""
echo "For automated DLQ drain, the ReprocessDeadLetterFunction runs every 5 minutes."
echo "Check App Insights for DLQ drain logs:"
echo "  traces | where message contains 'DLQ' | order by timestamp desc"
