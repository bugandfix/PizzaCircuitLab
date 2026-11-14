namespace PizzaCircuitLab.Web.Models;

// DTO with public serializable properties. No service references or streams.
public sealed class PizzaDraft
{
    public string CustomerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Pizza { get; set; } = "Margherita";
    public string Size { get; set; } = "Medium";
    public int Quantity { get; set; } = 1;
    public bool ExtraCheese { get; set; }
    public bool Mushrooms { get; set; }
    public bool Olives { get; set; }
    public string Crust { get; set; } = "Classic";
    public string Fulfillment { get; set; } = "Delivery";
    public string Address { get; set; } = "";
    public string Notes { get; set; } = "";
}
