#r "nuget: Microsoft.EntityFrameworkCore.SqlServer, 9.0.0"
#r "nuget: Microsoft.EntityFrameworkCore, 9.0.0"
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

// Load connection string from appsettings.json
var json = File.ReadAllText("FastDrop.Api/appsettings.json");
using var doc = JsonDocument.Parse(json);
var connString = doc.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString();

Console.WriteLine("Connection string: " + connString);

using var conn = new SqlConnection(connString);
conn.Open();
using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT Id, Status FROM TransferSessions";
using var reader = cmd.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"Found Transfer: {reader.GetGuid(0)}, Status: {reader.GetInt32(1)}");
}
reader.Close();

cmd.CommandText = "SELECT Id, FileMetadataId, ChunkNumber FROM ChunkMetadata";
using var reader2 = cmd.ExecuteReader();
while (reader2.Read())
{
    Console.WriteLine($"Found Chunk: {reader2.GetGuid(0)}, File: {reader2.GetGuid(1)}, Num: {reader2.GetInt32(2)}");
}
reader2.Close();

