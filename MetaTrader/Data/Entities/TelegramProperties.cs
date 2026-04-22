using System;
using System.ComponentModel.DataAnnotations;

namespace MetaTrader.Data.Entities
{
    public class TelegramProperties
    {
        [Key]
        public int id { get; set; }
        public String chatid { get; set; }
        public String bottoken { get; set; }
    }
}
