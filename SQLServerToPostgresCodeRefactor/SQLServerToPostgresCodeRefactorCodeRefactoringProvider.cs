using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using System.Diagnostics;

namespace SQLServerToPostgresCodeRefactor
{
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(SqlServerToPostgresCodeRefactorCodeRefactoringProvider)), Shared]
    internal class SqlServerToPostgresCodeRefactorCodeRefactoringProvider : CodeRefactoringProvider
    {
        private readonly Dictionary<string, string> keywords = new Dictionary<string, string>()
        {
            {"SqlConnection", "NpgsqlConnection" },
            {"SqlCommand", "NpgsqlCommand" },
            {"SqlDataAdapter", "NpgsqlDataAdapter" },
            {"SqlDataReader", "NpgsqlDataReader" },
            {"SqlParameter", "NpgsqlParameter" },
            {"SqlDbType", "NpgsqlDbType" },
            {"SqlException", "NpgsqlException" }
        };
        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            try
            {
                var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);

                // Find the node at the selection.
                var node = root.FindNode(context.Span);

                // Only offer a refactoring if the selected node is a type declaration node.
                var typeDecl = node as TypeDeclarationSyntax;
                var typeDec2 = node as MethodDeclarationSyntax;
                var usingStatementDeclaration = node as UsingStatementSyntax;
                var identifierNameSyntax = node as IdentifierNameSyntax;
                var propertyDeclarationSyntax = node as PropertyDeclarationSyntax;
                var constructorDeclarationSyntax = node as ConstructorDeclarationSyntax;

                if (typeDecl == null && typeDec2 == null && usingStatementDeclaration == null
                && identifierNameSyntax == null && propertyDeclarationSyntax == null && constructorDeclarationSyntax == null)
                {
                    return;
                }

                if (typeDecl != null)
                {
                    
                    var actionConvertToPostgres = CodeAction.Create(Constants.CONVERT_TO_POSTGRES
                        , c => ConvertClassToPosgresAsync(context.Document));
                    context.RegisterRefactoring(actionConvertToPostgres);

                    var action = CodeAction.Create(Constants.EXTRACT_INLINE_QUERIES
                        , c => ExtractQueries(context.Document));
                    context.RegisterRefactoring(action);

                    var addEmptyMethodToFetch = CodeAction.Create(Constants.ADD_EMPTY_METHOD_TO_FETCH_FROM_PG
                        , c => AddEmptyMethodToFetchFromPg(context.Document));
                    context.RegisterRefactoring(addEmptyMethodToFetch);


                    var addEmptyMethodToUpdateOrInsertIntoPg = CodeAction.Create(Constants.ADD_EMPTY_METHOD_TO_UPDATE_PG
                        , c => AddEmptyMethodToUpdateOrInsertIntoPg(context.Document));
                    context.RegisterRefactoring(addEmptyMethodToUpdateOrInsertIntoPg);

                    var addEmptyMethodToFetchWithCursorFromPg = CodeAction.Create(Constants.ADD_EMPTY_METHOD_TO_FETCH_WITH_CURSOR_FROM_PG
                      , c => AddEmptyMethodToFetchWithCursorFromPg(context.Document));
                    context.RegisterRefactoring(addEmptyMethodToFetchWithCursorFromPg);

                }

                if (typeDec2 != null)
                {
                    
                    var action = CodeAction.Create(Constants.CONVERT_TO_POSTGRES
                        , c => ConvertToPosgresAsync(context.Document,  typeDec2));

                    
                    context.RegisterRefactoring(action);
                }

                if (usingStatementDeclaration != null && usingStatementDeclaration.Declaration != null)
                {
                    var semanticModel = await context.Document.GetSemanticModelAsync();
                    var variableType = semanticModel
                        .GetSymbolInfo(usingStatementDeclaration.Declaration.Type)
                        .Symbol;

                    if (variableType != null && variableType.ToString() == "Npgsql.NpgsqlCommand")
                    {
                        var commandVariables = usingStatementDeclaration.Declaration.Variables.Single();
                        var commandVariableName = "command";
                        if (commandVariables != null)
                        {
                            commandVariableName = commandVariables.Identifier.Text;
                        }

                        var actionAddTransaction = CodeAction.Create(Constants.PUT_SQL_COMMAND_IN_TRANSACTION
                            , c => AddTransactionAsync(context.Document, usingStatementDeclaration, commandVariableName));
                        context.RegisterRefactoring(actionAddTransaction);
                    }

                    if (variableType != null && variableType.ToString() == "Npgsql.NpgsqlDataReader")
                    {
                        var readerVariables = usingStatementDeclaration.Declaration.Variables.Single();
                        var readerVariableName = "reader";
                        if (readerVariables != null)
                        {
                            readerVariableName = readerVariables.Identifier.Text;
                        }
                        
                        var commandIdentifierUnderUsing = usingStatementDeclaration.Declaration.DescendantNodes()
                            .OfType<MemberAccessExpressionSyntax>().FirstOrDefault()
                            ;

                        var commandIdentifier = (commandIdentifierUnderUsing!=null) ? commandIdentifierUnderUsing.DescendantNodes()
                            .OfType<IdentifierNameSyntax>().FirstOrDefault(): null;

                        var actionAddTransaction = CodeAction.Create(Constants.ADD_PG_CURSOR_FETCH
                            , c => AddCursorRead(context.Document, usingStatementDeclaration, readerVariableName, commandIdentifier.Identifier.Text));
                        context.RegisterRefactoring(actionAddTransaction);
                    }
                }

                if (propertyDeclarationSyntax != null)
                {
                    var action = CodeAction.Create(Constants.CONVERT_TO_POSTGRES
                        , c => ConvertPropertyToPosgresAsync(context.Document, propertyDeclarationSyntax));
                    context.RegisterRefactoring(action);
                }

                if (constructorDeclarationSyntax != null)
                {
                    var action = CodeAction.Create(Constants.CONVERT_TO_POSTGRES, c => ConvertConstructorToPosgresAsync(context.Document, constructorDeclarationSyntax));
                    context.RegisterRefactoring(action);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                
            }
        }
        private async Task<Document> ConvertConstructorToPosgresAsync(Document document, ConstructorDeclarationSyntax constructorDeclarationSyntax)
        {
            try
            {
                //c.can
                var editor = await DocumentEditor.CreateAsync(document);
                var parameterList = constructorDeclarationSyntax.ParameterList.Parameters;
                foreach (var p in parameterList)
                {
                    foreach (var node in p.ChildNodes())
                    {
                        string value;
                        keywords.TryGetValue(node.ToString(), out value);
                        if (!string.IsNullOrEmpty(value))
                            editor.ReplaceNode(node, SyntaxFactory.IdentifierName(value));
                    }
                }
                var newDocument = editor.GetChangedDocument();
                return newDocument;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return document;
        }

        private async Task<Document> ConvertPropertyToPosgresAsync(Document document, PropertyDeclarationSyntax propertyDeclarationSyntax)
        {
            try
            {
                var editor = await DocumentEditor.CreateAsync(document);
                foreach (var node in propertyDeclarationSyntax.ChildNodes())
                {
                    string value;
                    keywords.TryGetValue(node.ToString(), out value);
                    if (!string.IsNullOrEmpty(value))
                        editor.ReplaceNode(node, SyntaxFactory.IdentifierName(value));
                }
                var newDocument = editor.GetChangedDocument();
                return newDocument;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return document;
        }

        private async Task<Document> ExtractQueries(Document document)
        {
            try
            {                
                var solution = document.Project.Solution;
                var projects = solution.Projects.ToList();
                List<string> inlineSqls = new List<string>();

                foreach (Project project in projects)
                {
                    var projectFiles = project.Documents.ToList();
                    foreach (Document doc in projectFiles)
                    {
                        var syntaxTree = await doc.GetSyntaxTreeAsync();
                        var root = syntaxTree.GetRoot();
                       
                        var sqlCommandNodes = root.DescendantNodes(node => true)
                        .OfType<ObjectCreationExpressionSyntax>().Where(i => i.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.Text == "SqlCommand"))
                        .ToList();

                        foreach (ObjectCreationExpressionSyntax sqlCommandExpression in sqlCommandNodes)
                        {
                            var arguments = sqlCommandExpression.ArgumentList.Arguments;
                            var token = arguments.FirstOrDefault().Expression.GetFirstToken();

                            var tokenKind = token.Kind();
                            if (tokenKind == SyntaxKind.StringLiteralToken)
                            {
                                inlineSqls.Add(token.ValueText);
                            }
                            else if (tokenKind == SyntaxKind.IdentifierToken)
                            {
                                var queryVariable = root.DescendantNodes().OfType<IdentifierNameSyntax>()
                                    .FirstOrDefault(i => i.Identifier == token);
                                var varDeclares = root.DescendantNodes().OfType<VariableDeclarationSyntax>()
                                    .FirstOrDefault(v => v.Variables.Any(b => b.Identifier.Text == queryVariable.Identifier.Text));
                                
                                    if (varDeclares != null && varDeclares.Type.GetText().ToString().Trim() != "StringBuilder")
                                    {
                                        var equalsClause = varDeclares.DescendantNodes().OfType<EqualsValueClauseSyntax>().Single().Value.GetText();
                                        inlineSqls.Add(equalsClause.ToString());
                                    }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return document;
        }

        private async Task<Document> AddCursorRead(Document document, UsingStatementSyntax sqlReaderDeclaration, 
            string readerVariableName, string commandIdentifier)
        {
            try
            {
                var editor = await DocumentEditor.CreateAsync(document);
                var blockStatement = sqlReaderDeclaration.Statement as BlockSyntax;

                #region Block code

                var newListDeclarationStatement = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.GenericName(
                        SyntaxFactory.Identifier("List")).WithTypeArgumentList(
                            SyntaxFactory.TypeArgumentList(
                                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                                    SyntaxFactory.PredefinedType(
                                        SyntaxFactory.Token(SyntaxKind.StringKeyword))))))
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList<VariableDeclaratorSyntax>(
                        SyntaxFactory.VariableDeclarator(
                            SyntaxFactory.Identifier("lstCursors"))
                .WithInitializer(
                    SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.GenericName(
                                SyntaxFactory.Identifier("List"))
                .WithTypeArgumentList(
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                            SyntaxFactory.PredefinedType(
                                SyntaxFactory.Token(SyntaxKind.StringKeyword))))))
                .WithArgumentList(
                    SyntaxFactory.ArgumentList()))))))
                .NormalizeWhitespace();

                var newCursorReaderStatement = SyntaxFactory.UsingStatement(
                SyntaxFactory.Block(
                    SyntaxFactory.WhileStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(readerVariableName),
                                SyntaxFactory.IdentifierName("Read"))),
                        SyntaxFactory.Block(
                            SyntaxFactory.SingletonList<StatementSyntax>(
                                SyntaxFactory.ExpressionStatement(
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            SyntaxFactory.IdentifierName("lstCursors"),
                                            SyntaxFactory.IdentifierName("Add")))
                                    .WithArgumentList(
                                        SyntaxFactory.ArgumentList(
                                            SyntaxFactory.SingletonSeparatedList<ArgumentSyntax>(
                                                SyntaxFactory.Argument(
                                                    SyntaxFactory.InvocationExpression(
                                                        SyntaxFactory.MemberAccessExpression(
                                                            SyntaxKind.SimpleMemberAccessExpression,
                                                            SyntaxFactory.ElementAccessExpression(
                                                                SyntaxFactory.IdentifierName(readerVariableName))
                                                            .WithArgumentList(
                                                                SyntaxFactory.BracketedArgumentList(
                                                                    SyntaxFactory.SingletonSeparatedList<ArgumentSyntax>(
                                                                        SyntaxFactory.Argument(
                                                                            SyntaxFactory.LiteralExpression(
                                                                                SyntaxKind.NumericLiteralExpression,
                                                                                SyntaxFactory.Literal(0)
                                                                            ))))),
                                                            SyntaxFactory.IdentifierName("ToString"))))))))))),
                                SyntaxFactory.ExpressionStatement(
                                     SyntaxFactory.InvocationExpression(
                                          SyntaxFactory.MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                SyntaxFactory.IdentifierName(readerVariableName),
                                                SyntaxFactory.IdentifierName("Close"))))))
                     .WithDeclaration(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("NpgsqlDataReader"))
                        .WithVariables(
                            SyntaxFactory.SingletonSeparatedList<VariableDeclaratorSyntax>(
                                SyntaxFactory.VariableDeclarator(
                                    SyntaxFactory.Identifier(readerVariableName))
                                .WithInitializer(
                                    SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.InvocationExpression(
                                            SyntaxFactory.MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                SyntaxFactory.IdentifierName(commandIdentifier),
                                                SyntaxFactory.IdentifierName("ExecuteReader"))))))))
                     .NormalizeWhitespace();

                var cursorStatement = SyntaxFactory.ExpressionStatement(
                 SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(commandIdentifier),
                        SyntaxFactory.IdentifierName("CommandText")),
                      SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.PredefinedType(
                                    SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                                SyntaxFactory.IdentifierName("Format")))
                    .WithArgumentList(
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList<ArgumentSyntax>(new SyntaxNodeOrToken[] {
                            SyntaxFactory.Argument (
                                SyntaxFactory.LiteralExpression (
                                    SyntaxKind.StringLiteralExpression,
                                    SyntaxFactory.Literal (
                                        "@\"FETCH ALL IN \"\"{0}\"\"\"",
                                        "FETCH ALL IN \"{0}\"" ) ) ),
                            SyntaxFactory.Token(SyntaxKind.CommaToken),
                            SyntaxFactory.Argument (
                                SyntaxFactory.ElementAccessExpression(
                                    SyntaxFactory.IdentifierName("lstCursors"))
                                .WithArgumentList(
                                    SyntaxFactory.BracketedArgumentList(
                                        SyntaxFactory.SingletonSeparatedList<ArgumentSyntax>(
                                            SyntaxFactory.Argument(
                                                SyntaxFactory.LiteralExpression(
                                                    SyntaxKind.NumericLiteralExpression,
                                                    SyntaxFactory.Literal(0)))))))})))))
                .NormalizeWhitespace();

                var cursorStatement2 = SyntaxFactory.ExpressionStatement(
                 SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(commandIdentifier),
                        SyntaxFactory.IdentifierName("CommandType")),
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("CommandType"),
                        SyntaxFactory.IdentifierName("Text")))).NormalizeWhitespace();

                var insideReader = SyntaxFactory.UsingStatement(
                SyntaxFactory.Block(blockStatement.Statements))
                .WithDeclaration(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("NpgsqlDataReader"))
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList<VariableDeclaratorSyntax>(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(readerVariableName))
                        .WithInitializer(
                            SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.IdentifierName(commandIdentifier),
                                        SyntaxFactory.IdentifierName("ExecuteReader"))))))))
                .NormalizeWhitespace();

                List<StatementSyntax> statements = new List<StatementSyntax>();
                statements.Add(cursorStatement);
                statements.Add(cursorStatement2);
                statements.Add(insideReader);

                var ifStatement = SyntaxFactory.IfStatement(
                 SyntaxFactory.BinaryExpression(
                    SyntaxKind.GreaterThanExpression,
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("lstCursors"),
                            SyntaxFactory.IdentifierName("Count"))),
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression,
                        SyntaxFactory.Literal(0))),
                 SyntaxFactory.Block(statements.ToArray()))
                .NormalizeWhitespace();

                #endregion

                editor.InsertBefore(sqlReaderDeclaration, new List<SyntaxNode>()
            {
                    newListDeclarationStatement.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")),
                    newCursorReaderStatement.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n"))
            });

                editor.ReplaceNode(sqlReaderDeclaration, ifStatement.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));

                var addLinqDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System.Linq")).NormalizeWhitespace();

                var syntaxTree = await document.GetSyntaxTreeAsync();
                var Root = syntaxTree.GetRoot();

                var compilationUnitSyntax = (CompilationUnitSyntax)(Root);

                if (compilationUnitSyntax.Usings.All(u => u.Name.GetText().ToString() != typeof(CancellationToken).Namespace))
                {
                    var usings = compilationUnitSyntax.Usings;

                    if (!usings.Any(a => a.Name.GetText().ToString() == "System.Linq"))
                    {
                        var lastUsing = compilationUnitSyntax.Usings.Last();
                        editor.InsertAfter(lastUsing, addLinqDirective);
                    }
                }

                var rootaFormatted = editor.GetChangedRoot().NormalizeWhitespace();
                var newDocument = editor.GetChangedDocument();
                return newDocument.WithSyntaxRoot(rootaFormatted);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return document;
        }

        private async Task<Document> AddTransactionAsync(Document document, UsingStatementSyntax sqlCommandDeclaration, string commandVariableName)
        {
            try
            {
                var editor = await DocumentEditor.CreateAsync(document);
                var blockStatement = sqlCommandDeclaration.Statement as BlockSyntax;
                var newstatements = blockStatement.AddStatements(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("tran"),
                                SyntaxFactory.IdentifierName("Commit"))))).NormalizeWhitespace();

                var insertTransaction = SyntaxFactory.UsingStatement(
                    SyntaxFactory.Block(newstatements.Statements).NormalizeWhitespace())
                .WithDeclaration(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("NpgsqlTransaction"))
                    .WithVariables(
                        SyntaxFactory.SingletonSeparatedList<VariableDeclaratorSyntax>(
                            SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier("tran"))
                            .WithInitializer(
                                SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.InvocationExpression(
                                        SyntaxFactory.MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            SyntaxFactory.MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                SyntaxFactory.IdentifierName(commandVariableName),
                                                SyntaxFactory.IdentifierName("Connection")),
                                                SyntaxFactory.IdentifierName("BeginTransaction")))))))).NormalizeWhitespace();

                var newBlock = SyntaxFactory.Block(insertTransaction).NormalizeWhitespace();
                editor.ReplaceNode(blockStatement, newBlock.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
                var rootaFormatted = editor.GetChangedRoot().NormalizeWhitespace();
                var newDocument = editor.GetChangedDocument();
                return newDocument.WithSyntaxRoot(rootaFormatted);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return document;
        }

        private async Task<Document> ConvertToPosgresAsync(Document document, MethodDeclarationSyntax typeDec2)
        {
            var syntaxTree = typeDec2.Body.SyntaxTree;
            var root = syntaxTree.GetRoot();
            try
            {
                var dbTypes = root.DescendantNodes(node => true)
                .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlDbType").ToList();

                var sqlConnections = root.DescendantNodes(node => true)
                .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlConnection")
                .ToList();

                var sqlCommands = root.DescendantNodes(node => true)
                .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlCommand")
                .ToList();

                var sqlDataReaders = root.DescendantNodes(node => true)
                .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlDataReader")
                .ToList();

                var sqlDataAdapters = root.DescendantNodes(node => true)
                .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlDataAdapter")
                .ToList();

                var sqlParameters = root.DescendantNodes(node => true)
                .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlParameter")
                .ToList();

                var sqlExceptionss = root.DescendantNodes(node => true)
                .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlException")
                .ToList();

                var editor = await DocumentEditor.CreateAsync(document);

                var addLinqDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Npgsql")).NormalizeWhitespace();

                var compilationUnitSyntax = (CompilationUnitSyntax)(root);
                if (compilationUnitSyntax.Usings.All(u => u.Name.GetText().ToString() != typeof(CancellationToken).Namespace))
                    CheckAndAddUsing("Npgsql", compilationUnitSyntax, addLinqDirective, editor);

                if (sqlConnections != null && sqlConnections.Count > 0)
                    ReplaceNodes("NpgsqlConnection", sqlConnections, editor, typeDec2, true);

                if (sqlCommands != null && sqlCommands.Count > 0)
                    ReplaceNodes("NpgsqlCommand", sqlCommands, editor, typeDec2, true);

                if (sqlDataAdapters != null && sqlDataAdapters.Count > 0)
                    ReplaceNodes("NpgsqlDataAdapter", sqlDataAdapters, editor, typeDec2, true);

                if (sqlDataReaders != null && sqlDataReaders.Count > 0)
                    ReplaceNodes("NpgsqlDataReader", sqlDataReaders, editor, typeDec2, true);

                if (sqlParameters != null && sqlParameters.Count > 0)
                    ReplaceNodes("NpgsqlParameter", sqlParameters, editor, typeDec2, true);

                var parametersAsExpressions = root.DescendantNodes(node => true).OfType<ExpressionStatementSyntax>().ToList();
                if (parametersAsExpressions != null)
                    ReplaceParametersAsExpressions(parametersAsExpressions, editor, typeDec2, true);

                var parameterNames = root.DescendantNodes(node => true).OfType<ObjectCreationExpressionSyntax>()
                    .Where(x => x.Type is IdentifierNameSyntax && ((IdentifierNameSyntax)x.Type).Identifier.Text.Equals("sqlparameter", StringComparison.OrdinalIgnoreCase)).ToList();

                if (parameterNames != null && parameterNames.Count > 0)
                    ReplaceParameterNodes(parameterNames, editor, typeDec2, true);

                if (dbTypes != null && dbTypes.Count > 0)
                {
                    var addDbTypeDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("NpgsqlTypes")).NormalizeWhitespace();
                    CheckAndAddUsing("NpgsqlTypes", compilationUnitSyntax, addDbTypeDirective, editor);

                    foreach (SyntaxNode node in dbTypes)
                    {
                        var checkParent = node.AncestorsAndSelf(true).OfType<MethodDeclarationSyntax>()
                            .FirstOrDefault(i => i.Identifier.Text == typeDec2.Identifier.Text);
                        if (checkParent != null)
                        {
                            if (node.HasTrailingTrivia)
                                editor.ReplaceNode(node, SyntaxFactory.IdentifierName("NpgsqlDbType"));
                            else
                                editor.ReplaceNode(node.Parent, SyntaxFactory.IdentifierName(GetAppropriateDbType(node)));
                        }
                    }
                }

                if (sqlExceptionss != null && sqlExceptionss.Count > 0)
                    ReplaceNodes("NpgsqlException", sqlExceptionss, editor, typeDec2, true);

                var newDocument = editor.GetChangedDocument();
                return newDocument;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return document;
        }

        private void ReplaceParametersAsExpressions(List<ExpressionStatementSyntax> parametersAsExpressions, DocumentEditor editor,
            MethodDeclarationSyntax typeDec2, bool checkParent)
        {
            foreach (var p in parametersAsExpressions)
            {
                var literals = p.DescendantNodes(node => true).OfType<LiteralExpressionSyntax>().ToList();
                if (literals != null && literals.Count > 0 && p.Expression is AssignmentExpressionSyntax
                    && ((AssignmentExpressionSyntax)p.Expression).Left is MemberAccessExpressionSyntax
                    && ((IdentifierNameSyntax)((MemberAccessExpressionSyntax)((AssignmentExpressionSyntax)p.Expression).Left).Name).Identifier.Text.Equals("parametername", StringComparison.OrdinalIgnoreCase))
                    ReplaceLiteralNodes(literals, editor, typeDec2, checkParent);
            }
        }

        private void ReplaceParameterNodes( List<ObjectCreationExpressionSyntax> nodes
             , DocumentEditor editor, MethodDeclarationSyntax typeDec2 = null, bool checkParent = false)
        {
            foreach (SyntaxNode node in nodes)
            {
                var literals = node.DescendantNodes(x => true).OfType<LiteralExpressionSyntax>()
                    .Where(x => x.Token.ValueText.Contains("@")).ToList();
                if (literals != null && literals.Count > 0)
                    ReplaceLiteralNodes(literals, editor, typeDec2, checkParent);
            }
        }

        private void ReplaceLiteralNodes( List<LiteralExpressionSyntax> nodes
             , DocumentEditor editor, MethodDeclarationSyntax typeDec2 = null, bool checkParent = false)
        {
            foreach (SyntaxNode node in nodes)
            {
                string paramName = ((LiteralExpressionSyntax)node).Token.ValueText;
                if (checkParent)
                {
                    var parent = node.AncestorsAndSelf(true).OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(i => i.Identifier.Text == typeDec2.Identifier.Text);
                    if (parent != null)
                        editor.ReplaceNode(node, SyntaxFactory.IdentifierName($"\"par_{paramName.ToLower().Substring(1, paramName.Length - 1)}\""));
                }
                else
                    editor.ReplaceNode(node, SyntaxFactory.IdentifierName($"\"par_{paramName.ToLower().Substring(1, paramName.Length - 1)}\""));
            }
        }

        private void ReplaceNodes(string identifier, List<IdentifierNameSyntax> nodes
             , DocumentEditor editor, MethodDeclarationSyntax typeDec2 = null, bool checkParent = false)
        {
            foreach (SyntaxNode node in nodes)
            {
                if (checkParent)
                {
                    var parent = node.AncestorsAndSelf(true).OfType<MethodDeclarationSyntax>()
                         .FirstOrDefault(i => i.Identifier.Text == typeDec2.Identifier.Text);
                    if (parent != null)
                    {
                        editor.ReplaceNode(node, SyntaxFactory.IdentifierName(identifier));
                    }
                }
                else
                    editor.ReplaceNode(node, SyntaxFactory.IdentifierName(identifier));
            }
        }

        private void CheckAndAddUsing(string usingDirective, CompilationUnitSyntax compilationUnitSyntax,
             UsingDirectiveSyntax addLinqDirective, DocumentEditor editor)
        {
            var usings = compilationUnitSyntax.Usings;
            if (!usings.Any(a => a.Name.GetText().ToString() == usingDirective))
            {
                var lastUsing = compilationUnitSyntax.Usings.Last();
                editor.InsertAfter(lastUsing, addLinqDirective);
            }
        }

        private async Task<Document> ConvertClassToPosgresAsync(Document document 
            //, CancellationToken cancellationToken
            )
        {
            try
            {
                
                var root = await document.GetSyntaxRootAsync();

                var dbTypes = root.DescendantNodes(node => true)
                    .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlDbType")
                    .ToList();

                var sqlConnections = root.DescendantNodes(node => true)
                    .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlConnection")
                    .ToList();

                var sqlCommands = root.DescendantNodes(node => true)
                    .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlCommand")
                    .ToList();

                var sqlDataReaders = root.DescendantNodes(node => true)
                    .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlDataReader")
                    .ToList();

                var sqlParameters = root.DescendantNodes(node => true)
                    .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlParameter")
                    .ToList();

                var sqlExceptionss = root.DescendantNodes(node => true)
                    .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlException")
                    .ToList();

                var sqlDataAdapters = root.DescendantNodes(node => true)
                     .OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == "SqlDataAdapter")
                     .ToList();

                var editor = await DocumentEditor.CreateAsync(document);
                var addLinqDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Npgsql")).NormalizeWhitespace();
                var syntaxTree = await document.GetSyntaxTreeAsync();
                var Root = syntaxTree.GetRoot();

                var compilationUnitSyntax = (CompilationUnitSyntax)(Root);

                if (compilationUnitSyntax.Usings.All(u => u.Name.GetText().ToString() != typeof(CancellationToken).Namespace))
                    CheckAndAddUsing("Npgsql", compilationUnitSyntax, addLinqDirective, editor);

                if (sqlConnections != null && sqlConnections.Count > 0)
                    ReplaceNodes("NpgsqlConnection", sqlConnections, editor);

                if (sqlCommands != null && sqlCommands.Count > 0)
                    ReplaceNodes("NpgsqlCommand", sqlCommands, editor);

                if (sqlDataReaders != null && sqlDataReaders.Count > 0)
                    ReplaceNodes("NpgsqlDataReader", sqlDataReaders, editor);

                if (sqlDataAdapters != null && sqlDataAdapters.Count > 0)
                    ReplaceNodes("NpgsqlDataAdapter", sqlDataAdapters, editor);

                if (sqlParameters != null && sqlParameters.Count > 0)
                    ReplaceNodes("NpgsqlParameter", sqlParameters, editor);

                var parametersAsExpressions = root.DescendantNodes(node => true).OfType<ExpressionStatementSyntax>().ToList();
                ReplaceParametersAsExpressions(parametersAsExpressions, editor, null, false);

                var parameterNames = root.DescendantNodes(node => true).OfType<ObjectCreationExpressionSyntax>()
                    .Where(x => x.Type is IdentifierNameSyntax && ((IdentifierNameSyntax)x.Type).Identifier.Text.Equals("sqlparameter", StringComparison.OrdinalIgnoreCase)).ToList();

                if (parameterNames != null && parameterNames.Any())
                    ReplaceParameterNodes( parameterNames, editor);

                if (dbTypes != null && dbTypes.Count > 0)
                {
                    var addDbTypeDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("NpgsqlTypes")).NormalizeWhitespace();
                    CheckAndAddUsing("NpgsqlTypes", compilationUnitSyntax, addDbTypeDirective, editor);

                    foreach (SyntaxNode node in dbTypes)
                    {
                        if (node.HasTrailingTrivia)
                            editor.ReplaceNode(node, SyntaxFactory.IdentifierName("NpgsqlDbType"));
                        else
                            editor.ReplaceNode(node.Parent, SyntaxFactory.IdentifierName(GetAppropriateDbType(node)));
                    }
                }

                if (sqlExceptionss != null && sqlExceptionss.Count > 0)
                    ReplaceNodes("NpgsqlException", sqlExceptionss, editor);

                var newDocument = editor.GetChangedDocument();
                return newDocument;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            return document;
        }

        private string GetAppropriateDbType(SyntaxNode node)
        {
            string dbType = string.Empty;
            switch (((MemberAccessExpressionSyntax)node.Parent).Name.ToString().ToLower())
            {
                case "int":
                    dbType = "NpgsqlDbType.Integer";
                    break;
                case "bit":
                    dbType = "NpgsqlDbType.Bit";
                    break;
                case "bigint":
                    dbType = "NpgsqlDbType.Bigint";
                    break;
                case "char":
                case "nchar":
                    dbType = "NpgsqlDbType.Char";
                    break;
                case "date":
                    dbType = "NpgsqlDbType.Date";
                    break;
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                case "timestamp":
                    dbType = "NpgsqlDbType.Timestamp";
                    break;
                case "datetimeoffset":
                    dbType = "NpgsqlDbType.TimestampTz";
                    break;
                case "decimal":
                    dbType = "NpgsqlDbType.Numeric";
                    break;
                case "float":
                    dbType = "NpgsqlDbType.Real";
                    break;
                case "text":
                case "ntext":
                    dbType = "NpgsqlDbType.Text";
                    break;
                case "smallint":
                case "tinyint":
                    dbType = "NpgsqlDbType.Smallint";
                    break;
                case "uniqueidentifier":
                    dbType = "NpgsqlDbType.Uuid";
                    break;
                case "xml":
                    dbType = "NpgsqlDbType.Xml";
                    break;
                case "money":
                case "smallmoney":
                    dbType = "NpgsqlDbType.Money";
                    break;
                default:
                    dbType = "NpgsqlDbType.Varchar";
                    break;
            }
            return dbType;
        }

        private async Task<Document> AddEmptyMethodToFetchFromPg(Document document)
        {
            try
            {
                var root = await document.GetSyntaxRootAsync();
                var editor = await DocumentEditor.CreateAsync(document);
                
                var classDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
               
                if (classDeclaration == null) return null;

                var methodToInsert = GetMethodDeclarationSyntax(returnTypeName: "void",
                  methodName: "GetDataFromPostgresDatabase", parameterTypes: new string[] { }, paramterNames: new string[] { }, AddNewFetchMethodBlock()
               
                  );
                var newClassDeclaration = classDeclaration.AddMembers(methodToInsert);
                var newRoot = root.ReplaceNode(classDeclaration, newClassDeclaration);             
                var newDocument = editor.GetChangedDocument();
                return newDocument.WithSyntaxRoot(newRoot);             
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return null;
        }


        private async Task<Document> AddEmptyMethodToFetchWithCursorFromPg(Document document)
        {
            try
            {
                var root = await document.GetSyntaxRootAsync();
                var editor = await DocumentEditor.CreateAsync(document);
                
                var classDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                
                if (classDeclaration == null) return null;

                var methodToInsert = GetMethodDeclarationSyntax(returnTypeName: "void",
                  methodName: "GetDataFromPostgresDatabaseUsingCursor", parameterTypes: new string[] { }, paramterNames: new string[] { }, AddNewFetchMethodWithCursorBlock()
                  
                  );
                var newClassDeclaration = classDeclaration.AddMembers(methodToInsert);
                var newRoot = root.ReplaceNode(classDeclaration, newClassDeclaration);               
                var newDocument = editor.GetChangedDocument();
                return newDocument.WithSyntaxRoot(newRoot);               
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return null;
        }

        private async Task<Document> AddEmptyMethodToUpdateOrInsertIntoPg(Document document)
        {
            try
            {
                var root = await document.GetSyntaxRootAsync();
                var editor = await DocumentEditor.CreateAsync(document);
               
                var classDeclaration = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                
                if (classDeclaration == null) return null;

                var methodToInsert = GetMethodDeclarationSyntax(returnTypeName: "void",
                  methodName: "UpdateOrInsertDataInPostgresDatabase", parameterTypes: new string[] { }, paramterNames: new string[] { }, AddNewUpdateMethodBlock()
                 
                  );
                var newClassDeclaration = classDeclaration.AddMembers(methodToInsert);
                var newRoot = root.ReplaceNode(classDeclaration, newClassDeclaration);

                var newDocument = editor.GetChangedDocument();
                return newDocument.WithSyntaxRoot(newRoot);                
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            return null;
        }

        public MethodDeclarationSyntax GetMethodDeclarationSyntax(string returnTypeName, string methodName, string[] parameterTypes, string[] paramterNames, BlockSyntax block)
        {
            var parameterList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(GetParametersList(parameterTypes, paramterNames)));
            return SyntaxFactory.MethodDeclaration(attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                          modifiers: SyntaxFactory.TokenList(),
                          returnType: SyntaxFactory.ParseTypeName(returnTypeName),
                          explicitInterfaceSpecifier: null,
                          identifier: SyntaxFactory.Identifier(methodName),
                          typeParameterList: null,
                          parameterList: parameterList,
                          constraintClauses: SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                          body: block ,
                         
                          semicolonToken: SyntaxFactory.Token(SyntaxKind.None)
                          )                  
                  .WithAdditionalAnnotations(Formatter.Annotation);
        }

        private IEnumerable<ParameterSyntax> GetParametersList(string[] parameterTypes, string[] paramterNames)
        {
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                yield return SyntaxFactory.Parameter(attributeLists: SyntaxFactory.List<AttributeListSyntax>(),
                                                         modifiers: SyntaxFactory.TokenList(),
                                                         type: SyntaxFactory.ParseTypeName(parameterTypes[i]),
                                                         identifier: SyntaxFactory.Identifier(paramterNames[i]),
                                                         @default: null);
            }
        }

        private BlockSyntax AddNewFetchMethodBlock()
        {
            var variableDeclaration = LocalDeclarationStatement(
            VariableDeclaration(
             PredefinedType(
              Token(SyntaxKind.StringKeyword)))
        .WithVariables(
          SingletonSeparatedList<VariableDeclaratorSyntax>(
              VariableDeclarator(
                  Identifier("connectionString"))
              .WithInitializer(
                  EqualsValueClause(
                      MemberAccessExpression(
                          SyntaxKind.SimpleMemberAccessExpression,
                          IdentifierName("String"),
                          IdentifierName("Empty")))))))
        .NormalizeWhitespace();

            var variableSqlStatementDeclaration = LocalDeclarationStatement(
                VariableDeclaration(
                 PredefinedType(
                  Token(SyntaxKind.StringKeyword)))
            .WithVariables(
              SingletonSeparatedList<VariableDeclaratorSyntax>(
                  VariableDeclarator(
                      Identifier("sqlStatement"))
                  .WithInitializer(
                      EqualsValueClause(
                          MemberAccessExpression(
                              SyntaxKind.SimpleMemberAccessExpression,
                              IdentifierName("String"),
                              IdentifierName("Empty")))))))
            .NormalizeWhitespace();


            var sqlconnectionUsing = TryStatement(
    SingletonList<CatchClauseSyntax>(
        CatchClause()
        .WithDeclaration(
            CatchDeclaration(
                IdentifierName("Exception"))
            .WithIdentifier(
                Identifier("ex")))))
.WithBlock(
    Block(
        SingletonList<StatementSyntax>(
            UsingStatement(
                Block(
                    SingletonList<StatementSyntax>(
                        UsingStatement(
                            Block(
                                ExpressionStatement(
                                    AssignmentExpression(
                                        SyntaxKind.SimpleAssignmentExpression,
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            IdentifierName("cmd"),
                                            IdentifierName("CommandType")),
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            IdentifierName("CommandType"),
                                            IdentifierName("Text")))),
                                ExpressionStatement(
                                    InvocationExpression(
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            IdentifierName("con"),
                                            IdentifierName("Open")))),
                                UsingStatement(
                                    Block(
                                        SingletonList<StatementSyntax>(
                                            IfStatement(
                                                InvocationExpression(
                                                    MemberAccessExpression(
                                                        SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName("reader"),
                                                        IdentifierName("Read"))),
                                                Block(
                                                    SingletonList<StatementSyntax>(
                                                        LocalDeclarationStatement(
                                                            VariableDeclaration(
                                                                IdentifierName(
                                                                    Identifier(
                                                                        TriviaList(),
                                                                        SyntaxKind.VarKeyword,
                                                                        "var",
                                                                        "var",
                                                                        TriviaList())))
                                                            .WithVariables(
                                                                SingletonSeparatedList<VariableDeclaratorSyntax>(
                                                                    VariableDeclarator(
                                                                        Identifier("text"))
                                                                    .WithInitializer(
                                                                        EqualsValueClause(
                                                                            InvocationExpression(
                                                                                MemberAccessExpression(
                                                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                                                    IdentifierName("Convert"),
                                                                                    IdentifierName("ToString")))
                                                                            .WithArgumentList(
                                                                                ArgumentList(
                                                                                    SingletonSeparatedList<ArgumentSyntax>(
                                                                                        Argument(
                                                                                            ElementAccessExpression(
                                                                                                IdentifierName("reader"))
                                                                                            .WithArgumentList(
                                                                                                BracketedArgumentList(
                                                                                                    SingletonSeparatedList<ArgumentSyntax>(
                                                                                                        Argument(
                                                                                                            LiteralExpression(
                                                                                                                SyntaxKind.NumericLiteralExpression,
                                                                                                                Literal(0)))))))))))))))))))))
                                .WithDeclaration(
                                    VariableDeclaration(
                                        IdentifierName("NpgsqlDataReader"))
                                    .WithVariables(
                                        SingletonSeparatedList<VariableDeclaratorSyntax>(
                                            VariableDeclarator(
                                                Identifier("reader"))
                                            .WithInitializer(
                                                EqualsValueClause(
                                                    InvocationExpression(
                                                        MemberAccessExpression(
                                                            SyntaxKind.SimpleMemberAccessExpression,
                                                            IdentifierName("cmd"),
                                                            IdentifierName("ExecuteReader")))))))),
                                ExpressionStatement(
                                    InvocationExpression(
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            IdentifierName("con"),
                                            IdentifierName("Close"))))))
                        .WithDeclaration(
                            VariableDeclaration(
                                IdentifierName(
                                    Identifier(
                                        TriviaList(),
                                        SyntaxKind.VarKeyword,
                                        "var",
                                        "var",
                                        TriviaList())))
                            .WithVariables(
                                SingletonSeparatedList<VariableDeclaratorSyntax>(
                                    VariableDeclarator(
                                        Identifier("cmd"))
                                    .WithInitializer(
                                        EqualsValueClause(
                                            ObjectCreationExpression(
                                                IdentifierName("NpgsqlCommand"))
                                            .WithArgumentList(
                                                ArgumentList(
                                                    SeparatedList<ArgumentSyntax>(
                                                        new SyntaxNodeOrToken[]{
                                                            Argument(
                                                                IdentifierName("sqlStatement")),
                                                            Token(SyntaxKind.CommaToken),
                                                            Argument(
                                                                IdentifierName("con"))})))))))))))
            .WithDeclaration(
                VariableDeclaration(
                    IdentifierName(
                        Identifier(
                            TriviaList(),
                            SyntaxKind.VarKeyword,
                            "var",
                            "var",
                            TriviaList())))
                .WithVariables(
                    SingletonSeparatedList<VariableDeclaratorSyntax>(
                        VariableDeclarator(
                            Identifier("con"))
                        .WithInitializer(
                            EqualsValueClause(
                                ObjectCreationExpression(
                                    IdentifierName("NpgsqlConnection"))
                                .WithArgumentList(
                                    ArgumentList(
                                        SingletonSeparatedList<ArgumentSyntax>(
                                            Argument(
                                                IdentifierName("connectionString")))))))))))))
            .NormalizeWhitespace();

            List <StatementSyntax> statements = new List<StatementSyntax>();
            
            statements.Add(variableDeclaration.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
            statements.Add(variableSqlStatementDeclaration.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
            statements.Add(sqlconnectionUsing);
            
            var block = SyntaxFactory.Block(statements.ToArray());

            return block;
        }

        private BlockSyntax AddNewUpdateMethodBlock()
        {
            var variableDeclaration = LocalDeclarationStatement(
            VariableDeclaration(
             PredefinedType(
              Token(SyntaxKind.StringKeyword)))
        .WithVariables(
          SingletonSeparatedList<VariableDeclaratorSyntax>(
              VariableDeclarator(
                  Identifier("connectionString"))
              .WithInitializer(
                  EqualsValueClause(
                      MemberAccessExpression(
                          SyntaxKind.SimpleMemberAccessExpression,
                          IdentifierName("String"),
                          IdentifierName("Empty")))))))
        .NormalizeWhitespace();

            var variableSqlStatementDeclaration = LocalDeclarationStatement(
                VariableDeclaration(
                 PredefinedType(
                  Token(SyntaxKind.StringKeyword)))
            .WithVariables(
              SingletonSeparatedList<VariableDeclaratorSyntax>(
                  VariableDeclarator(
                      Identifier("sqlStatement"))
                  .WithInitializer(
                      EqualsValueClause(
                          MemberAccessExpression(
                              SyntaxKind.SimpleMemberAccessExpression,
                              IdentifierName("String"),
                              IdentifierName("Empty")))))))
            .NormalizeWhitespace();


            var sqlconnectionUsing = TryStatement(
    SingletonList<CatchClauseSyntax>(
        CatchClause()
        .WithDeclaration(
            CatchDeclaration(
                IdentifierName("Exception"))
            .WithIdentifier(
                Identifier("ex")))))
.WithBlock(
    Block(
        SingletonList<StatementSyntax>(
            UsingStatement(
                Block(
                    UsingStatement(
                        Block(
                            ExpressionStatement(
                                AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("cmd"),
                                        IdentifierName("CommandType")),
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("CommandType"),
                                        IdentifierName("Text")))),
                            ExpressionStatement(
                                InvocationExpression(
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("con"),
                                        IdentifierName("Open")))),
                            ExpressionStatement(
                                InvocationExpression(
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        IdentifierName("cmd"),
                                        IdentifierName("ExecuteNonQuery"))))))
                    .WithDeclaration(
                        VariableDeclaration(
                            IdentifierName(
                                Identifier(
                                    TriviaList(),
                                    SyntaxKind.VarKeyword,
                                    "var",
                                    "var",
                                    TriviaList())))
                        .WithVariables(
                            SingletonSeparatedList<VariableDeclaratorSyntax>(
                                VariableDeclarator(
                                    Identifier("cmd"))
                                .WithInitializer(
                                    EqualsValueClause(
                                        ObjectCreationExpression(
                                            IdentifierName("NpgsqlCommand"))
                                        .WithArgumentList(
                                            ArgumentList(
                                                SeparatedList<ArgumentSyntax>(
                                                    new SyntaxNodeOrToken[]{
                                                        Argument(
                                                            IdentifierName("sqlStatement")),
                                                        Token(SyntaxKind.CommaToken),
                                                        Argument(
                                                            IdentifierName("con"))})))))))),
                    ExpressionStatement(
                        InvocationExpression(
                            MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                IdentifierName("con"),
                                IdentifierName("Close"))))))
            .WithDeclaration(
                VariableDeclaration(
                    IdentifierName(
                        Identifier(
                            TriviaList(),
                            SyntaxKind.VarKeyword,
                            "var",
                            "var",
                            TriviaList())))
                .WithVariables(
                    SingletonSeparatedList<VariableDeclaratorSyntax>(
                        VariableDeclarator(
                            Identifier("con"))
                        .WithInitializer(
                            EqualsValueClause(
                                ObjectCreationExpression(
                                    IdentifierName("NpgsqlConnection"))
                                .WithArgumentList(
                                    ArgumentList(
                                        SingletonSeparatedList<ArgumentSyntax>(
                                            Argument(
                                                IdentifierName("connectionString")))))))))))))
            .NormalizeWhitespace();




            List<StatementSyntax> statements = new List<StatementSyntax>();
            
            statements.Add(variableDeclaration.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
            statements.Add(variableSqlStatementDeclaration.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
            statements.Add(sqlconnectionUsing);

            var block = SyntaxFactory.Block(statements.ToArray());
            return block;
        }

        private BlockSyntax AddNewFetchMethodWithCursorBlock()
        {
            var variableDeclaration = LocalDeclarationStatement(
            VariableDeclaration(
             PredefinedType(
              Token(SyntaxKind.StringKeyword)))
        .WithVariables(
          SingletonSeparatedList<VariableDeclaratorSyntax>(
              VariableDeclarator(
                  Identifier("connectionString"))
              .WithInitializer(
                  EqualsValueClause(
                      MemberAccessExpression(
                          SyntaxKind.SimpleMemberAccessExpression,
                          IdentifierName("String"),
                          IdentifierName("Empty")))))))
        .NormalizeWhitespace();


            var variableSqlStatementDeclaration = LocalDeclarationStatement(
                VariableDeclaration(
                 PredefinedType(
                  Token(SyntaxKind.StringKeyword)))
            .WithVariables(
              SingletonSeparatedList<VariableDeclaratorSyntax>(
                  VariableDeclarator(
                      Identifier("sqlStatement"))
                  .WithInitializer(
                      EqualsValueClause(
                          MemberAccessExpression(
                              SyntaxKind.SimpleMemberAccessExpression,
                              IdentifierName("String"),
                              IdentifierName("Empty")))))))
            .NormalizeWhitespace();


            var sqlconnectionUsing = TryStatement(
    SingletonList<CatchClauseSyntax>(
        CatchClause()
        .WithDeclaration(
            CatchDeclaration(
                IdentifierName("Exception"))
            .WithIdentifier(
                Identifier("ex")))))
.WithBlock(
    Block(
        LocalDeclarationStatement(
            VariableDeclaration(
                PredefinedType(
                    Token(SyntaxKind.StringKeyword)))
            .WithVariables(
                SingletonSeparatedList<VariableDeclaratorSyntax>(
                    VariableDeclarator(
                        Identifier("strConnectionstring"))
                    .WithInitializer(
                        EqualsValueClause(
                            MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                PredefinedType(
                                    Token(SyntaxKind.StringKeyword)),
                                IdentifierName("Empty"))))))),
        ExpressionStatement(
            InvocationExpression(
                IdentifierName("using"))
            .WithArgumentList(
                ArgumentList(
                    SeparatedList<ArgumentSyntax>(
                        new SyntaxNodeOrToken[]{
                            Argument(
                                IdentifierName("NpgsqlConnection")),
                            MissingToken(SyntaxKind.CommaToken),
                            Argument(
                                AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    IdentifierName("conn"),
                                    ObjectCreationExpression(
                                        IdentifierName("NpgsqlConnection"))
                                    .WithArgumentList(
                                        ArgumentList(
                                            SingletonSeparatedList<ArgumentSyntax>(
                                                Argument(
                                                    IdentifierName("strConnectionstring")))))))}))))
        .WithSemicolonToken(
            MissingToken(SyntaxKind.SemicolonToken)),
        Block(
            SingletonList<StatementSyntax>(
                UsingStatement(
                    Block(
                        SingletonList<StatementSyntax>(
                            UsingStatement(
                                Block(
                                    ExpressionStatement(
                                        AssignmentExpression(
                                            SyntaxKind.SimpleAssignmentExpression,
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                IdentifierName("cmd"),
                                                IdentifierName("CommandType")),
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    MemberAccessExpression(
                                                        SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName("System"),
                                                        IdentifierName("Data")),
                                                    IdentifierName("CommandType")),
                                                IdentifierName("StoredProcedure")))),
                                    ExpressionStatement(
                                        InvocationExpression(
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    IdentifierName("cmd"),
                                                    IdentifierName("Parameters")),
                                                IdentifierName("AddWithValue")))
                                        .WithArgumentList(
                                            ArgumentList(
                                                SeparatedList<ArgumentSyntax>(
                                                    new SyntaxNodeOrToken[]{
                                                        Argument(
                                                            LiteralExpression(
                                                                SyntaxKind.StringLiteralExpression,
                                                                Literal("<parametername>"))),
                                                        Token(SyntaxKind.CommaToken),
                                                        Argument(
                                                            LiteralExpression(
                                                                SyntaxKind.StringLiteralExpression,
                                                                Literal("<parametervalue>")))})))),
                                    ExpressionStatement(
                                        InvocationExpression(
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                IdentifierName("conn"),
                                                IdentifierName("Open")))),
                                    LocalDeclarationStatement(
                                        VariableDeclaration(
                                            GenericName(
                                                Identifier("List"))
                                            .WithTypeArgumentList(
                                                TypeArgumentList(
                                                    SingletonSeparatedList<TypeSyntax>(
                                                        PredefinedType(
                                                            Token(SyntaxKind.StringKeyword))))))
                                        .WithVariables(
                                            SingletonSeparatedList<VariableDeclaratorSyntax>(
                                                VariableDeclarator(
                                                    Identifier("lstCursors"))
                                                .WithInitializer(
                                                    EqualsValueClause(
                                                        ObjectCreationExpression(
                                                            GenericName(
                                                                Identifier("List"))
                                                            .WithTypeArgumentList(
                                                                TypeArgumentList(
                                                                    SingletonSeparatedList<TypeSyntax>(
                                                                        PredefinedType(
                                                                            Token(SyntaxKind.StringKeyword))))))
                                                        .WithArgumentList(
                                                            ArgumentList())))))),
                                    UsingStatement(
                                        Block(
                                            WhileStatement(
                                                InvocationExpression(
                                                    MemberAccessExpression(
                                                        SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName("dr"),
                                                        IdentifierName("Read"))),
                                                Block(
                                                    SingletonList<StatementSyntax>(
                                                        ExpressionStatement(
                                                            InvocationExpression(
                                                                MemberAccessExpression(
                                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                                    IdentifierName("lstCursors"),
                                                                    IdentifierName("Add")))
                                                            .WithArgumentList(
                                                                ArgumentList(
                                                                    SingletonSeparatedList<ArgumentSyntax>(
                                                                        Argument(
                                                                            InvocationExpression(
                                                                                MemberAccessExpression(
                                                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                                                    ElementAccessExpression(
                                                                                        IdentifierName("dr"))
                                                                                    .WithArgumentList(
                                                                                        BracketedArgumentList(
                                                                                            SingletonSeparatedList<ArgumentSyntax>(
                                                                                                Argument(
                                                                                                    LiteralExpression(
                                                                                                        SyntaxKind.NumericLiteralExpression,
                                                                                                        Literal(0)))))),
                                                                                    IdentifierName("ToString"))))))))))),
                                            ExpressionStatement(
                                                InvocationExpression(
                                                    MemberAccessExpression(
                                                        SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName("dr"),
                                                        IdentifierName("Close"))))))
                                    .WithDeclaration(
                                        VariableDeclaration(
                                            IdentifierName("NpgsqlDataReader"))
                                        .WithVariables(
                                            SingletonSeparatedList<VariableDeclaratorSyntax>(
                                                VariableDeclarator(
                                                    Identifier("dr"))
                                                .WithInitializer(
                                                    EqualsValueClause(
                                                        InvocationExpression(
                                                            MemberAccessExpression(
                                                                SyntaxKind.SimpleMemberAccessExpression,
                                                                IdentifierName("cmd"),
                                                                IdentifierName("ExecuteReader")))))))),
                                    ExpressionStatement(
                                        AssignmentExpression(
                                            SyntaxKind.SimpleAssignmentExpression,
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                IdentifierName("cmd"),
                                                IdentifierName("CommandText")),
                                            InvocationExpression(
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    PredefinedType(
                                                        Token(SyntaxKind.StringKeyword)),
                                                    IdentifierName("Format")))
                                            .WithArgumentList(
                                                ArgumentList(
                                                    SeparatedList<ArgumentSyntax>(
                                                        new SyntaxNodeOrToken[]{
                                                            Argument(
                                                                LiteralExpression(
                                                                    SyntaxKind.StringLiteralExpression,
                                                                    Literal(
                                                                        @"@""FETCH ALL IN """"{0}""""""",
                                                                        "FETCH ALL IN \"{0}\""))),
                                                            Token(SyntaxKind.CommaToken),
                                                            Argument(
                                                                ElementAccessExpression(
                                                                    IdentifierName("lstCursors"))
                                                                .WithArgumentList(
                                                                    BracketedArgumentList(
                                                                        SingletonSeparatedList<ArgumentSyntax>(
                                                                            Argument(
                                                                                LiteralExpression(
                                                                                    SyntaxKind.NumericLiteralExpression,
                                                                                    Literal(0)))))))}))))),
                                    ExpressionStatement(
                                        AssignmentExpression(
                                            SyntaxKind.SimpleAssignmentExpression,
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                IdentifierName("cmd"),
                                                IdentifierName("CommandType")),
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    MemberAccessExpression(
                                                        SyntaxKind.SimpleMemberAccessExpression,
                                                        IdentifierName("System"),
                                                        IdentifierName("Data")),
                                                    IdentifierName("CommandType")),
                                                IdentifierName("Text")))),
                                    UsingStatement(
                                        Block(
                                            SingletonList<StatementSyntax>(
                                                IfStatement(
                                                    InvocationExpression(
                                                        MemberAccessExpression(
                                                            SyntaxKind.SimpleMemberAccessExpression,
                                                            IdentifierName("reader"),
                                                            IdentifierName("Read"))),
                                                    Block(
                                                        SingletonList<StatementSyntax>(
                                                            LocalDeclarationStatement(
                                                                VariableDeclaration(
                                                                    IdentifierName(
                                                                        Identifier(
                                                                            TriviaList(),
                                                                            SyntaxKind.VarKeyword,
                                                                            "var",
                                                                            "var",
                                                                            TriviaList())))
                                                                .WithVariables(
                                                                    SingletonSeparatedList<VariableDeclaratorSyntax>(
                                                                        VariableDeclarator(
                                                                            Identifier("text"))
                                                                        .WithInitializer(
                                                                            EqualsValueClause(
                                                                                ConditionalExpression(
                                                                                    InvocationExpression(
                                                                                        MemberAccessExpression(
                                                                                            SyntaxKind.SimpleMemberAccessExpression,
                                                                                            IdentifierName("reader"),
                                                                                            IdentifierName("IsDBNull")))
                                                                                    .WithArgumentList(
                                                                                        ArgumentList(
                                                                                            SingletonSeparatedList<ArgumentSyntax>(
                                                                                                Argument(
                                                                                                    LiteralExpression(
                                                                                                        SyntaxKind.NumericLiteralExpression,
                                                                                                        Literal(0)))))),
                                                                                    LiteralExpression(
                                                                                        SyntaxKind.NullLiteralExpression),
                                                                                    InvocationExpression(
                                                                                        MemberAccessExpression(
                                                                                            SyntaxKind.SimpleMemberAccessExpression,
                                                                                            ElementAccessExpression(
                                                                                                IdentifierName("reader"))
                                                                                            .WithArgumentList(
                                                                                                BracketedArgumentList(
                                                                                                    SingletonSeparatedList<ArgumentSyntax>(
                                                                                                        Argument(
                                                                                                            LiteralExpression(
                                                                                                                SyntaxKind.NumericLiteralExpression,
                                                                                                                Literal(0)))))),
                                                                                            IdentifierName("ToString")))))))))))))))
                                    .WithDeclaration(
                                        VariableDeclaration(
                                            IdentifierName("NpgsqlDataReader"))
                                        .WithVariables(
                                            SingletonSeparatedList<VariableDeclaratorSyntax>(
                                                VariableDeclarator(
                                                    Identifier("reader"))
                                                .WithInitializer(
                                                    EqualsValueClause(
                                                        InvocationExpression(
                                                            MemberAccessExpression(
                                                                SyntaxKind.SimpleMemberAccessExpression,
                                                                IdentifierName("cmd"),
                                                                IdentifierName("ExecuteReader"))))))))))
                            .WithDeclaration(
                                VariableDeclaration(
                                    IdentifierName("NpgsqlCommand"))
                                .WithVariables(
                                    SingletonSeparatedList<VariableDeclaratorSyntax>(
                                        VariableDeclarator(
                                            Identifier("cmd"))
                                        .WithInitializer(
                                            EqualsValueClause(
                                                ObjectCreationExpression(
                                                    IdentifierName("NpgsqlCommand"))
                                                .WithArgumentList(
                                                    ArgumentList(
                                                        SeparatedList<ArgumentSyntax>(
                                                            new SyntaxNodeOrToken[]{
                                                                Argument(
                                                                    LiteralExpression(
                                                                        SyntaxKind.StringLiteralExpression,
                                                                        Literal("<storedprocedure>"))),
                                                                Token(SyntaxKind.CommaToken),
                                                                Argument(
                                                                    IdentifierName("conn")),
                                                                Token(SyntaxKind.CommaToken),
                                                                Argument(
                                                                    IdentifierName("tran"))})))))))))))
                .WithDeclaration(
                    VariableDeclaration(
                        IdentifierName("NpgsqlTransaction"))
                    .WithVariables(
                        SingletonSeparatedList<VariableDeclaratorSyntax>(
                            VariableDeclarator(
                                Identifier("tran"))
                            .WithInitializer(
                                EqualsValueClause(
                                    InvocationExpression(
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            IdentifierName("conn"),
                                            IdentifierName("BeginTransaction"))))))))))))
.NormalizeWhitespace();




            List<StatementSyntax> statements = new List<StatementSyntax>();
            
            statements.Add(variableDeclaration.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
            statements.Add(variableSqlStatementDeclaration.WithTrailingTrivia(SyntaxFactory.EndOfLine("\r\n")));
            statements.Add(sqlconnectionUsing);

            var block = SyntaxFactory.Block(statements.ToArray());

            return block;
        }

    }
}
