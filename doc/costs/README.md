# Cost estimate artifacts

This directory contains an Azure Pricing Calculator screenshot and exported workbook committed on 2025-12-07:

- [`Azure Pricing Calculator.png`](Azure%20Pricing%20Calculator.png)
- [`ExportedEstimate.xlsx`](ExportedEstimate.xlsx)

Treat both as a historical sizing snapshot, not a current price quote. Azure prices vary by region, redundancy, capacity, access tier, operations, retrieval, networking, and support plan. The running architecture and lifecycle policy can also change independently of these files.

Before using the estimate for a budget:

1. Confirm the current resources in [`deploy/bicep/main.bicep`](../../deploy/bicep/main.bicep).
2. Confirm the lifecycle and retention behavior in [Technical design](../TECHNICAL_DESIGN.md#12-lifecycle-and-cost-behavior).
3. Recreate the scenario in the [Azure Pricing Calculator](https://azure.microsoft.com/en-us/pricing/calculator/) with the target region and expected storage/operation volumes.
4. Include Archive rehydration/retrieval and early-deletion charges in restore scenarios.
