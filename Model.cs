using System;
using System.Collections.Generic;

namespace KoalaNotes;

public class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString().Substring(0, 8);
    public string Title { get; set; } = "Untitled Note";
    public string Body { get; set; } = "";
    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}

public class CategoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString().Substring(0, 8);
    public string Label { get; set; } = "New Category";
    public List<NoteItem> Notes { get; set; } = new List<NoteItem>();
}