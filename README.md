## Introduction
- This tool is a Visual Studio extension which helps migrating the Microsoft C# ADO.NET code that is connected to Microsoft SQL Server to PostgreSQL database. 
- This extension is built using Roslyn framework (https://docs.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/) and .Net Standard 2.0. 
- Along with converting the existing code, this tool also extracts inline queries if required. 
- It will download and install the required NuGet packages related to PostgreSQL.

## Pre-requisites:

- Visual Studio 
- Visual Studio extension development
- .NET framework that supports ADO.NET

## Supported functionalities:

- Modifies the ADO.NET C# Microsoft SQL Server application code to support PostgreSQL
- Extract inline queries

# Installation and using the tool:
Install the vsix file to add this tool as extension to Visual Studio.

## Using the tool:

1. Hover the C# component that require conversion. Select "Convert To Postgres".
2. The tool displays the changes that it is going to apply to the existing C# code.
3. Select the option again to apply the changes.

At present, the tool applies the below C# components:

1. Class
2. Method
3. Property
4. Constructor
5. ADO.NET code

All ADO.NET SQLClient related components(`SQLConnection`, `SQLCommand`, `SQLDataReader`, `SQLDataAdapter`, `SQLDataTypes`, `SQLParameter`, 
`SQLException`) will be converted to corresponding PostgreSQL Npgsql components (`NpgsqlConnection`, `NpgsqlCommand`, `NpgsqlDataReader`, 
`NpgsqlDataAdapter`, `NpgsqlDataTypes`, `NpgsqlParameters`, `NpgsqlException`) using this extension.

## Testing:
Build and run the SQLServerToPostgresCodeRefactor.Vsix project. When the application asks for a solution to open, navigate to VsixTestProject folder and open the solution.
The solution created only for testing the conversion. It may not build successfully.
The SampleClass.cs has test code that can be used for testing the tool.

The tool is still in developing stage and enhancements and extensions are still in progress.

## Security

See [CONTRIBUTING](CONTRIBUTING.md#security-issue-notifications) for more information.

## License

This library is licensed under the MIT-0 License. See the LICENSE file.

