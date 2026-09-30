using DevExpress.ExpressApp;
using Shop.Module.BusinessObjects;

namespace Shop.Module.Automation;

// The generated-rules shape: a plain static class, a lambda handler, and the type tests two calls away.
public static class Rules
{
    public static void Hook(IObjectSpace objectSpace)
    {
        objectSpace.Committing += (s, a) => Apply(objectSpace);
    }

    private static void Apply(IObjectSpace os) => ApplyRules(os);

    private static void ApplyRules(IObjectSpace os)
    {
        foreach (var saved in os.GetObjectsToSave(false))
        {
            if (saved is global::Shop.Module.BusinessObjects.Order order) { }
            var invoice = saved as Invoice;
        }
    }
}
