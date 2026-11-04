using ReviewGate.Contracts;
using ReviewGate.Testing;

namespace ReviewGate.StateTests;
public sealed class ResolutionTests
{
    [Fact] public void AssumedCostIsSeparateFromUsageAndUnknownCannotBeRefundedByDeclaration()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var r=s.NewRun(p);var a=s.Reserve(r,p,p.Requests[0],e.Grant(p));s.Dispatch(a.Id);s.Unknown(a.Id,"dispatch_outcome_unknown");
        s.Resolve(r,p,new(Data.Version,p.Hash,[new(a.Id,"conservative_full_reservation","synthetic assumption","tester",DateTimeOffset.UtcNow)]));
        var cost=s.Costs(r.Id);Assert.Equal(0,cost["usage_calculated_usd"]!.GetValue<decimal>());Assert.Equal(a.ReservationUsd,cost["assumed_usd"]!.GetValue<decimal>());Assert.Null(cost["billed_usd"]);
    }
    [Fact] public void UsageResolutionRequiresUsageAndPinnedPriceCalculation()
    {
        using var e=new TestEnvironment();using var s=e.Store();var p=e.MakePlan(s);var r=s.NewRun(p);var a=s.Reserve(r,p,p.Requests[0],e.Grant(p));s.Dispatch(a.Id);s.Unknown(a.Id,"unknown");
        var item=new ResolutionItem(a.Id,"usage_confirmed","fixed mock usage","tester",DateTimeOffset.UtcNow,.00014m);
        Assert.Equal("resolution_usage_required",Assert.Throws<ReviewError>(()=>s.Resolve(r,p,new(Data.Version,p.Hash,[item]))).Code);
        Assert.Equal("resolution_cost_mismatch",Assert.Throws<ReviewError>(()=>s.Resolve(r,p,new(Data.Version,p.Hash,[item with{Usage=new(1,1)}]))).Code);
        s.Resolve(r,p,new(Data.Version,p.Hash,[item with{Usage=new(100,20)}]));Assert.Equal(.00014m,s.Attempts(r.Id).Single().CostUsd);Assert.Empty(s.Units(r.Id));
    }
}
