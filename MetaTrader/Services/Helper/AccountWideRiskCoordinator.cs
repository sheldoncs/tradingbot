namespace MetaTrader.Services.Helper
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    namespace SentinelFX
    {
        /// <summary>
        /// Account-level ENTRY gate. It does not close trades, change stops, or replace
        /// the existing per-symbol UnifiedRiskManagementEngine.
        ///
        /// IMPORTANT: Every bot that can submit an order on this account must acquire
        /// the SAME cross-process/distributed lease, then refresh the broker snapshot
        /// while holding that lease. A process-local lock is NOT sufficient.
        /// </summary>
        public sealed class AccountWideRiskCoordinator
        {
            private readonly IAccountRiskSnapshotProvider _snapshots;
            private readonly IAccountRiskLeaseProvider _leases;
            private readonly AccountRiskLimits _limits;

            public AccountWideRiskCoordinator(
                IAccountRiskSnapshotProvider snapshots,
                IAccountRiskLeaseProvider leases,
                AccountRiskLimits limits)
            {
                _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
                _leases = leases ?? throw new ArgumentNullException(nameof(leases));
                _limits = limits ?? throw new ArgumentNullException(nameof(limits));
                _limits.Validate();
            }

            /// <summary>
            /// Serializes the FINAL entry check AND actual broker order submission.
            /// Execute the broker order in submitOrder, not after this call returns.
            /// A rejected/failed broker order must return a failed OrderSubmissionResult.
            /// The provider must count open positions and pending orders from ALL bots.
            /// </summary>
            public EntryGateResult TrySubmit(
                NewTradeRisk proposed,
                Func<OrderSubmissionResult> submitOrder)
            {
                if (proposed == null) throw new ArgumentNullException(nameof(proposed));
                if (submitOrder == null) throw new ArgumentNullException(nameof(submitOrder));
                proposed.Validate();

                // The lease must be account-scoped and shared across ALL processes/hosts.
                // A null lease fails closed. Do not use a local static lock here.
                using (var lease = _leases.TryAcquire(_limits.AccountKey, _limits.LeaseWait))
                {
                    if (lease == null || !lease.IsHeld)
                        return EntryGateResult.Reject("Account-wide entry lease unavailable");

                    // Snapshot MUST be obtained AFTER lease acquisition. It must reflect
                    // broker state, not merely positions cached by the invoking bot.
                    AccountRiskSnapshot snapshot;
                    try { snapshot = _snapshots.ReadFresh(_limits.AccountKey); }
                    catch (Exception ex)
                    {
                        return EntryGateResult.Reject("Broker risk snapshot failed: " + ex.Message);
                    }

                    var verdict = Evaluate(snapshot, proposed);
                    if (!verdict.Allowed) return verdict;

                    // Hold the lease until broker acknowledgment so a second bot cannot
                    // approve an entry using an outdated exposure total.
                    try
                    {
                        var result = submitOrder();
                        if (result == null || !result.Succeeded)
                            return EntryGateResult.Reject("Broker did not confirm entry: " +
                                (result == null ? "null order response" : result.Detail));
                        return EntryGateResult.Permit("Order confirmed: " + result.Detail);
                    }
                    catch (Exception ex)
                    {
                        // Broker outcome could be unknown; do NOT blindly retry here.
                        return EntryGateResult.Reject("Order outcome must be reconciled with MT5: " + ex.Message);
                    }
                }
            }

            public EntryGateResult Evaluate(AccountRiskSnapshot snapshot, NewTradeRisk proposed)
            {
                if (proposed == null) return EntryGateResult.Reject("Missing proposal");
                try { proposed.Validate(); }
                catch (Exception ex) { return EntryGateResult.Reject(ex.Message); }

                if (snapshot == null || !snapshot.IsComplete || !snapshot.IsFresh)
                    return EntryGateResult.Reject("Incomplete or stale account-wide broker snapshot");
                if (!string.Equals(snapshot.AccountKey, _limits.AccountKey, StringComparison.Ordinal))
                    return EntryGateResult.Reject("Account identity mismatch");
                if (snapshot.EquityUsd <= 0 || double.IsNaN(snapshot.EquityUsd) || double.IsInfinity(snapshot.EquityUsd))
                    return EntryGateResult.Reject("Invalid account equity");
                if (snapshot.RealizedTodayUsd > 0 && double.IsNaN(snapshot.RealizedTodayUsd))
                    return EntryGateResult.Reject("Invalid realized P/L");
                if (!IsFinite(snapshot.RealizedTodayUsd))
                    return EntryGateResult.Reject("Invalid daily realized P/L");
                if (!IsFinite(snapshot.OpenFloatingPnlUsd))
                    return EntryGateResult.Reject("Invalid floating P/L");

                var positions = snapshot.OpenPositionsAndPendingOrders;
                if (positions == null)
                    return EntryGateResult.Reject("Missing all-account exposure list");
                double existingRisk = 0;
                foreach (var p in positions)
                {
                    if (p == null || !IsFinite(p.WorstCaseAdditionalLossUsd) ||
                        p.WorstCaseAdditionalLossUsd < 0 || !p.HasVerifiedBrokerStop)
                        return EntryGateResult.Reject("Open/pending order has unknown risk or missing verified broker stop");
                    existingRisk += p.WorstCaseAdditionalLossUsd;
                    if (!IsFinite(existingRisk)) return EntryGateResult.Reject("Invalid aggregate risk");
                }

                // Realized losses plus current floating loss trigger a new-entry pause.
                // This is an entry gate, not an automatic liquidation mechanism.
                double dayPnl = snapshot.RealizedTodayUsd + snapshot.OpenFloatingPnlUsd;
                if (dayPnl <= -_limits.MaxDailyDrawdownUsd)
                    return EntryGateResult.Reject("Daily realized+floating drawdown limit reached");

                if (positions.Count >= _limits.MaxConcurrentOrders)
                    return EntryGateResult.Reject("Maximum number of account positions/orders reached");

                double limit = Math.Min(_limits.MaxAggregateRiskUsd,
                    snapshot.EquityUsd * _limits.MaxAggregateRiskFractionOfEquity);
                if (existingRisk + proposed.WorstCaseLossUsd > limit + 1e-9)
                    return EntryGateResult.Reject(string.Format(
                        "Combined worst-case loss {0:F2} exceeds budget {1:F2} (existing {2:F2}, proposed {3:F2})",
                        existingRisk + proposed.WorstCaseLossUsd, limit, existingRisk, proposed.WorstCaseLossUsd));

                return EntryGateResult.Permit(string.Format(
                    "Within risk budget: existing {0:F2}, proposed {1:F2}, cap {2:F2}",
                    existingRisk, proposed.WorstCaseLossUsd, limit));
            }

            private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public sealed class AccountRiskLimits
        {
            public string AccountKey { get; set; }
            public double MaxAggregateRiskUsd { get; set; }
            public double MaxAggregateRiskFractionOfEquity { get; set; }
            public double MaxDailyDrawdownUsd { get; set; }
            public int MaxConcurrentOrders { get; set; }
            public TimeSpan LeaseWait { get; set; } = TimeSpan.FromSeconds(5);

            public void Validate()
            {
                if (string.IsNullOrWhiteSpace(AccountKey)) throw new ArgumentException("Set the MT5 account-specific AccountKey");
                if (!Positive(MaxAggregateRiskUsd) || !Positive(MaxAggregateRiskFractionOfEquity) ||
                    MaxAggregateRiskFractionOfEquity > 1 || !Positive(MaxDailyDrawdownUsd) ||
                    MaxConcurrentOrders < 1 || LeaseWait <= TimeSpan.Zero)
                    throw new ArgumentException("Invalid account risk limits");
            }
            private static bool Positive(double x) => x > 0 && !double.IsInfinity(x) && !double.IsNaN(x);
        }

        public sealed class AccountRiskSnapshot
        {
            public string AccountKey { get; set; }
            public bool IsComplete { get; set; } // ALL positions and pending orders, including manual/other EAs
            public bool IsFresh { get; set; }    // provider verifies recency and MT5 connectivity
            public double EquityUsd { get; set; }
            public double RealizedTodayUsd { get; set; } // account-server day, after charges
            public double OpenFloatingPnlUsd { get; set; } // account-wide, after known charges
            public IReadOnlyList<ExistingOrderRisk> OpenPositionsAndPendingOrders { get; set; }
        }

        public sealed class ExistingOrderRisk
        {
            public string Symbol { get; set; }
            public bool HasVerifiedBrokerStop { get; set; }
            /// <summary>
            /// Maximum additional loss FROM CURRENT STATE until broker SL, measured in
            /// account USD, with adverse price movement, spread and estimated charges.
            /// This should NOT be clamped to zero if risk is unknown; mark stop unverified.
            /// Conservative upper bound; actual slippage can exceed it.
            /// </summary>
            public double WorstCaseAdditionalLossUsd { get; set; }
        }

        public sealed class NewTradeRisk
        {
            public string Symbol { get; set; }
            public double WorstCaseLossUsd { get; set; } // entry to VERIFIED initial broker SL, incl. costs
            public void Validate()
            {
                if (string.IsNullOrWhiteSpace(Symbol) || WorstCaseLossUsd <= 0 ||
                    double.IsNaN(WorstCaseLossUsd) || double.IsInfinity(WorstCaseLossUsd))
                    throw new ArgumentException("Invalid trade risk; an entry with missing verified stop is not allowed");
            }
        }

        public sealed class OrderSubmissionResult
        {
            public bool Succeeded { get; set; }
            public string Detail { get; set; }
        }

        public sealed class EntryGateResult
        {
            public bool Allowed { get; private set; }
            public string Reason { get; private set; }
            public static EntryGateResult Permit(string reason) => new EntryGateResult { Allowed = true, Reason = reason };
            public static EntryGateResult Reject(string reason) => new EntryGateResult { Allowed = false, Reason = reason };
        }

        public interface IAccountRiskSnapshotProvider
        {
            AccountRiskSnapshot ReadFresh(string accountKey);
        }

        public interface IAccountRiskLease : IDisposable
        {
            bool IsHeld { get; }
        }

        public interface IAccountRiskLeaseProvider
        {
            // Must provide exclusive ACCOUNT-SCOPED ownership across every bot process.
            // Redis SET NX PX + token-verified release, a DB transaction/advisory lock,
            // or another supported distributed lock are candidates. No no-op leases.
            IAccountRiskLease TryAcquire(string accountKey, TimeSpan wait);
        }
    }

}
