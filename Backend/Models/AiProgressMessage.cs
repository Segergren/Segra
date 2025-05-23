﻿namespace Segra.Backend.Models
{
    public class AiProgressMessage
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public int Progress { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public Content Content { get; set; }
    }
}
