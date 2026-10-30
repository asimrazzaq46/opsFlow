using System.Net;
using OpsFlow.Tests.Integration.Support;

namespace OpsFlow.Tests.Integration;

public class CustomerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Owners_and_managers_manage_customers_but_agents_and_customers_cannot()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var email = $"new-{Guid.NewGuid():N}@example.com";

        var created = await w.Manager.ReadAsync<CustomerJson>(await w.Manager.PostAsync(w.Url("customers"), new { name = "Globex", email, phone = "123" }));
        var found = await w.Owner.ReadAsync<PagedDto<CustomerJson>>(await w.Owner.GetAsync(w.Url("customers?search=globex")));
        var updated = await w.Owner.ReadAsync<CustomerJson>(await w.Owner.PutAsync(w.Url($"customers/{created.Id}"), new { name = "Globex Corp", email, phone = (string?)null }));
        (await w.Manager.PutAsync(w.Url($"customers/{created.Id}/status"), new { isActive = false })).EnsureSuccessStatusCode();
        var inactive = await w.Owner.ReadAsync<PagedDto<CustomerJson>>(await w.Owner.GetAsync(w.Url("customers?isActive=false")));

        Assert.Single(found.Items, c => c.Id == created.Id);
        Assert.Equal("Globex Corp", updated.Name);
        Assert.Single(inactive.Items, c => c.Id == created.Id && !c.IsActive);
        foreach (var outsider in new[] { w.Agent1, w.Customer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(w.Url("customers"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(w.Url($"customers/{created.Id}"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsync(w.Url("customers"), new { name = "X", email = "x@example.com" })).StatusCode);
        }
    }

    [Fact]
    public async Task Duplicate_and_invalid_customer_data_is_rejected()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await w.Manager.ReadAsync<CustomerJson>(await w.Manager.PostAsync(w.Url("customers"), new { name = "First", email }));

        Assert.Equal(HttpStatusCode.Conflict, (await w.Manager.PostAsync(w.Url("customers"), new { name = "Second", email = email.ToUpperInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(w.Url("customers"), new { name = "Bad", email = "not-an-email" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Manager.PostAsync(w.Url("customers"), new { name = "", email = "ok@example.com" })).StatusCode);
    }

    [Fact]
    public async Task A_customer_can_only_be_linked_to_an_active_customer_member_and_only_once()
    {
        var w = await TicketWorld.CreateAsync(factory);

        var agentLink = await w.Manager.PostAsync(w.Url("customers"), new { name = "Bad link", email = $"l1-{Guid.NewGuid():N}@example.com", linkedUserId = w.Agent1.UserId });
        var unknownLink = await w.Manager.PostAsync(w.Url("customers"), new { name = "Bad link", email = $"l2-{Guid.NewGuid():N}@example.com", linkedUserId = Guid.NewGuid() });
        var secondContactForSameUser = await w.Manager.PostAsync(w.Url("customers"), new { name = "Twin", email = $"l3-{Guid.NewGuid():N}@example.com", linkedUserId = w.Customer.UserId });

        Assert.Equal(HttpStatusCode.BadRequest, agentLink.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownLink.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondContactForSameUser.StatusCode);
    }

    [Fact]
    public async Task An_email_match_alone_never_gives_a_customer_user_access()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var unlinkedUser = await ApiClient.AddMemberAsync(factory, w.Owner, w.Org.Id, "Customer");
        // a contact with the SAME email as the signed-in customer user, but not linked
        var lookalike = await w.Manager.ReadAsync<CustomerJson>(await w.Manager.PostAsync(w.Url("customers"), new { name = "Lookalike", email = unlinkedUser.Email }));
        var ticket = await w.CreateTicketAsync(w.Manager, new { title = "For the lookalike", description = "d", categoryId = w.CategoryId, customerId = lookalike.Id });

        var visible = await unlinkedUser.ReadAsync<PagedDto<TicketSummaryJson>>(await unlinkedUser.GetAsync(w.Url("tickets")));

        Assert.Empty(visible.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await unlinkedUser.GetAsync(w.Url($"tickets/{ticket.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await unlinkedUser.PostAsync(w.Url("tickets"), new { title = "x y z", description = "d", categoryId = w.CategoryId })).StatusCode);
    }

    [Fact]
    public async Task Customers_are_isolated_between_tenants()
    {
        var a = await TicketWorld.CreateAsync(factory);
        var b = await TicketWorld.CreateAsync(factory);
        var customerOfA = await a.CreateCustomerAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await b.Owner.GetAsync(a.Url($"customers/{customerOfA.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.GetAsync(b.Url($"customers/{customerOfA.Id}"))).StatusCode); // id substitution under own tenant
        Assert.Equal(HttpStatusCode.NotFound, (await b.Owner.PutAsync(b.Url($"customers/{customerOfA.Id}/status"), new { isActive = false })).StatusCode);
        // a ticket may never reference another tenant's customer
        Assert.Equal(HttpStatusCode.BadRequest, (await b.TryCreateTicketAsync(b.Manager, new { title = "Cross tenant", description = "d", categoryId = b.CategoryId, customerId = customerOfA.Id })).StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_contact_keeps_ticket_history_and_blocks_new_work()
    {
        var w = await TicketWorld.CreateAsync(factory);
        var ticket = await w.CreateTicketAsync(w.Customer);

        (await w.Manager.PutAsync(w.Url($"customers/{w.CustomerContactId}/status"), new { isActive = false })).EnsureSuccessStatusCode();

        Assert.Equal(w.CustomerContactId, (await w.GetTicketAsync(w.Manager, ticket.Id)).Summary.CustomerId); // history intact
        Assert.Equal(HttpStatusCode.OK, (await w.Customer.GetAsync(w.Url($"tickets/{ticket.Id}"))).StatusCode); // still viewable
        Assert.Equal(HttpStatusCode.Forbidden, (await w.TryCreateTicketAsync(w.Customer, new { title = "More work", description = "d", categoryId = w.CategoryId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.TryCreateTicketAsync(w.Manager, new { title = "On behalf", description = "d", categoryId = w.CategoryId, customerId = w.CustomerContactId })).StatusCode);
    }
}
