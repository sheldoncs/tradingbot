using System.ComponentModel.DataAnnotations;

namespace MetaTrader.Data.Entities
{
    public class KryptoLabels
    {
        [Key]
        public int id { get; set; }
        public string label { get; set; }
    }
}
