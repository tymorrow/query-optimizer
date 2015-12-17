﻿namespace Qo.Parsing
{
    using Microsoft.SqlServer.TransactSql.ScriptDom;
    using QueryModel;
    using RelationalModel;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using TSql = Microsoft.SqlServer.TransactSql.ScriptDom;

    public class QoParser
    {
        private readonly IConsole _console;
        private readonly Schema _schema;
        private string _lastQueryString;
        private readonly List<string> _setOperators = new List<string>
        {
            "union",
            "intersect",
            "except"
        };
        private int _queryCounter;
        private Node _tree = new Node();
        
        public QoParser()
        {
            _console = new Console();
            _schema = Resources.Schemas.GetSchema1();
        }
        public QoParser(Schema schema)
        {
            _console = new Console();
            _schema = schema;
        }
        public QoParser(IConsole console, Schema schema)
        {
            _console = console;
            _schema = schema;
        }

        public bool Parse(string query)
        {
            var success = false;
            _tree = new Node();
            // Sanitize original query string
            query = query.Replace("’", "'").Replace("`", "'").Replace("‘", "'");
            try
            {
                IList<ParseError> errors;
                var reader = new StringReader(query);
                var parser = new TSql120Parser(false);
                var script = parser.Parse(reader, out errors) as TSqlScript;

                if (errors.Any())
                {
                    var sb = new StringBuilder();
                    foreach (var e in errors)
                    {
                        sb.AppendLine(e.Message);
                    }
                    _console.WriteLine(sb.ToString());
                }
                else
                {
                    var batch = script.Batches.First();
                    var statement = batch.Statements.First();
                    var result = ProcessStatement(statement);
                    if(result is Query)
                    {
                        var unboxedResult = result as Query;
                        var queryTree = unboxedResult.GetQueryTree();
                        var ra = queryTree.ToString();
                        _console.WriteLine(ra);
                    }
                    if(result is MultiQuery)
                    {
                        var unboxedResult = result as MultiQuery;
                        var queryTree = unboxedResult.GetQueryTree();
                        var ra = queryTree.ToString();
                        _console.WriteLine(ra);
                    }
                    success = true;
                }
            }
            catch (Exception e)
            {
                _console.WriteLine(e.Message);
            }
            
            return success;
        }
        
        #region TSqlParser Methods

        public dynamic ProcessStatement(TSqlStatement statement)
        {
            // Could be Stored Procedure, While, or If but for this project, we're not expecting those.
            // if(statement is TSql.WhileStatement)
            //     ProcessStatements(((TSql.WhileStatement)statement).Statement);
            if (statement is TSql.SelectStatement)
                return ProcessQueryExpression(((TSql.SelectStatement)statement).QueryExpression);
            return null;
        }

        public dynamic ProcessQueryExpression(QueryExpression exp)
        {            
            if (exp is QuerySpecification) // Actual SELECT Statement
            {
                return ProcessQuerySpecification(exp as QuerySpecification);
            }
            else if (exp is BinaryQueryExpression) // UNION, INTERSECT, EXCEPT
            {
                return ProcessBinaryQueryExpression(exp as BinaryQueryExpression);
            }
            else if (exp is QueryParenthesisExpression) // SELECT statement surrounded by paranthesis
            {
                var par = exp as QueryParenthesisExpression;
                return ProcessQueryExpression(par.QueryExpression);
            }
            else
            {
                throw new Exception("QueryExpression type could not be identified.");
            }
        }

        public dynamic ProcessQuerySpecification(QuerySpecification spec)
        {
            var query = new Query();
            MultiQuery multiQuery;

            #region SELECT

            _console.WriteLine("SELECT");
            Debug.Indent();
            foreach (SelectScalarExpression exp in spec.SelectElements)
            {
                var att = ProcessSelectScalarExpression(exp);
                query.Select.Attributes.Add(att);
            }
            Debug.Unindent();

            #endregion
            #region FROM 

            _console.WriteLine("FROM");
            Debug.Indent();
            foreach (NamedTableReference t in spec.FromClause.TableReferences)
            {
                var tableName = t.SchemaObject.BaseIdentifier.Value;
                var relation = _schema.Relations.Single(r => r.Name == tableName);
                query.From.Relations.Add(relation);

                if (t.Alias != null)
                {
                    if (!relation.Aliases.Contains(t.Alias.ToString()))
                        relation.Aliases.Add(t.Alias.Value);
                    _console.Write(t.Alias.Value + " ");
                }
                _console.Write(t.SchemaObject.BaseIdentifier.Value);
                _console.WriteLine(string.Empty);
            }
            Debug.Unindent();

            #endregion
            #region WHERE

            _console.WriteLine("WHERE");
            Debug.Indent();
            var whereClauseExpression = spec.WhereClause.SearchCondition;
            if (whereClauseExpression is BooleanBinaryExpression)
            {
                #region BooleanBinary Expression

                var whereClause = whereClauseExpression as BooleanBinaryExpression;
                var tuple = ProcessBooleanBinaryExpression(whereClause);
                query.Where = new WhereStatement
                {
                    Conditions = tuple.Item1,
                    Operators = tuple.Item2,
                };

                #endregion
            }
            else if (whereClauseExpression is BooleanComparisonExpression)
            {
                #region Comparison Expression
                var whereClause = whereClauseExpression as BooleanComparisonExpression;

                if (whereClause.FirstExpression is ColumnReferenceExpression && 
                    whereClause.SecondExpression is ColumnReferenceExpression)
                {
                    var condition = ProcessBooleanComparisonExpression(whereClause);
                    query.Where.Conditions.Add(condition);
                }
                else if(whereClause.FirstExpression is ColumnReferenceExpression &&
                        whereClause.SecondExpression is ScalarSubquery)
                {
                    multiQuery = new MultiQuery();
                    var leftExp = whereClause.FirstExpression as ColumnReferenceExpression;
                    var rightExp = whereClause.SecondExpression as ScalarSubquery;
                    var att = ProcessColumnReferenceExpression(leftExp) as QueryModel.Attribute;
                    dynamic rightQueryExp = ProcessQueryExpression(rightExp.QueryExpression);

                    if (rightQueryExp is Query)
                    {
                        var rightQuery = rightQueryExp as Query;
                        // Add comparison to where clause of subquery 
                        ModifyQueryDueToComparison(rightQuery, att, whereClause.ComparisonType);
                        // Build multi-query
                        multiQuery.Queries.Add(query);
                        multiQuery.Queries.Add(rightQuery);
                        var tuple = new Tuple<dynamic, dynamic>(query, rightQuery);
                        multiQuery.Operators.Add(tuple, SetOperator.Division);
                        // Adjust for NOT EXISTS
                        var neMultiQuery = new MultiQuery();
                        var neTuple = new Tuple<dynamic, dynamic>(query, multiQuery);
                        neMultiQuery.Operators.Add(tuple, SetOperator.Except);
                        neMultiQuery.Queries.Add(query);
                        neMultiQuery.Queries.Add(multiQuery);
                        return neMultiQuery;
                    }
                    else if (rightQueryExp is MultiQuery)
                    {
                        var rightMultiQuery = rightQueryExp as MultiQuery;
                        // Add comparison to where clause of left subquery
                        ModifyQueryDueToComparison(rightMultiQuery.Queries[0], att, whereClause.ComparisonType);
                        // Add comparison to where clause of right subquery
                        ModifyQueryDueToComparison(rightMultiQuery.Queries[1], att, whereClause.ComparisonType);
                        // Build/return multi-query
                        multiQuery.Queries.Add(query);
                        multiQuery.Queries.Add(rightMultiQuery);
                        var tuple = new Tuple<dynamic, dynamic>(query, rightMultiQuery);
                        multiQuery.Operators.Add(tuple, SetOperator.Division);
                        // Adjust for NOT EXISTS
                        var neMultiQuery = new MultiQuery();
                        var neTuple = new Tuple<dynamic, dynamic>(query, multiQuery);
                        neMultiQuery.Operators.Add(neTuple, SetOperator.Except);
                        neMultiQuery.Queries.Add(query);
                        neMultiQuery.Queries.Add(multiQuery);
                        return neMultiQuery;
                    }
                }


                #endregion
            }
            else if (whereClauseExpression is InPredicate)
            {
                #region IN Predicate
                MultiQuery rightMultiQuery;
                dynamic rightQueryExp;
                var whereClause = whereClauseExpression as InPredicate;
                var expression = whereClause.Expression as ColumnReferenceExpression;
                var subQuery = whereClause.Subquery as ScalarSubquery;
                var att = ProcessColumnReferenceExpression(expression) as QueryModel.Attribute;
                multiQuery = new MultiQuery();
                multiQuery.Queries.Add(query);

                _console.WriteLine("IN ");
                _console.WriteLine("(");
                Debug.Indent();
                rightQueryExp = ProcessQueryExpression(subQuery.QueryExpression);
                Debug.Unindent();
                _console.WriteLine(")");
                
                if (rightQueryExp is Query)
                {
                    var rightQuery = rightQueryExp as Query;
                    // Add comparison to where clause of subquery 
                    ModifyQueryDueToIn(rightQuery, att);
                    // Build/return multi-query
                    multiQuery.Queries.Add(rightQuery);
                    var tuple = new Tuple<dynamic, dynamic>(query, rightQuery);
                    multiQuery.Operators.Add(tuple, SetOperator.Division);
                    return multiQuery;
                }
                else if(rightQueryExp is MultiQuery)
                {
                    rightMultiQuery = rightQueryExp as MultiQuery;
                    // Add comparison to where clause of left subquery
                    ModifyQueryDueToIn(rightMultiQuery.Queries[0], att);
                    // Add comparison to where clause of right subquery
                    ModifyQueryDueToIn(rightMultiQuery.Queries[1], att);
                    // Build/return multi-query
                    multiQuery.Queries.Add(rightMultiQuery);
                    var tuple = new Tuple<dynamic, dynamic>(query, rightMultiQuery);
                    multiQuery.Operators.Add(tuple, SetOperator.Division);
                    return multiQuery;
                }
                #endregion
            }
            else
            {
                throw new NotImplementedException("WhereClause type not found.");
            }
            Debug.Unindent();

            #endregion
            #region GROUP BY

            // Group By Expression
            if (spec.GroupByClause != null)
            {
                _console.WriteLine("GROUP BY");
                Debug.Indent();
                var groupByClause = spec.GroupByClause;
                foreach (ExpressionGroupingSpecification gSpec in groupByClause.GroupingSpecifications)
                {
                    if (gSpec.Expression is ColumnReferenceExpression)
                    {
                        ProcessColumnReferenceExpression(gSpec.Expression as ColumnReferenceExpression);
                    }
                }
                Debug.Unindent();
                _console.WriteLine(string.Empty);
            }

            #endregion
            #region HAVING

            // Having Expression
            if (spec.HavingClause != null)
            {
                _console.WriteLine("HAVING ");
                Debug.Indent();
                var havingClauseExpression = spec.HavingClause.SearchCondition;
                if (havingClauseExpression is BooleanBinaryExpression)
                {
                    ProcessBooleanBinaryExpression(havingClauseExpression as BooleanBinaryExpression);
                }
                else if (havingClauseExpression is BooleanComparisonExpression)
                {
                    ProcessBooleanComparisonExpression(havingClauseExpression as BooleanComparisonExpression);
                }
                else
                {
                    throw new NotImplementedException("HavingClause type not found.");
                }
                Debug.Unindent();
            }

            #endregion

            return query;
        }

        private void ModifyQueryDueToIn(Query query, QueryModel.Attribute att)
        {
            var condition = new Condition
            {
                LeftSide = att,
                Operator = BooleanComparisonType.Equals
            };
            condition.RightSide = query.Select.Attributes.First();
            if (query.Where.Conditions.Any())
            {
                var condTuple = new Tuple<Condition, Condition>(query.Where.Conditions.Last(), condition);
                query.Where.Operators.Add(condTuple, BooleanBinaryExpressionType.And);
            }
            query.Where.Conditions.Add(condition);
        }

        private void ModifyQueryDueToComparison(Query query, QueryModel.Attribute att, BooleanComparisonType op)
        {
            var condition = new Condition
            {
                LeftSide = att
            };
            switch(op)
            {
                case BooleanComparisonType.Equals:
                    condition.Operator = BooleanComparisonType.NotEqualToExclamation; break;
                case BooleanComparisonType.NotEqualToExclamation:
                    condition.Operator = BooleanComparisonType.Equals; break;
                case BooleanComparisonType.GreaterThan:
                    condition.Operator = BooleanComparisonType.LessThanOrEqualTo; break;
                case BooleanComparisonType.GreaterThanOrEqualTo:
                    condition.Operator = BooleanComparisonType.LessThan; break;
                case BooleanComparisonType.LessThan:
                    condition.Operator = BooleanComparisonType.GreaterThanOrEqualTo; break;
                case BooleanComparisonType.LessThanOrEqualTo:
                    condition.Operator = BooleanComparisonType.GreaterThan; break;
            }
            condition.RightSide = query.Select.Attributes.First();
            if (query.Where.Conditions.Any())
            {
                var condTuple = new Tuple<Condition, Condition>(query.Where.Conditions.Last(), condition);
                query.Where.Operators.Add(condTuple, BooleanBinaryExpressionType.And);
            }
            query.Where.Conditions.Add(condition);
        }

        public MultiQuery ProcessBinaryQueryExpression(BinaryQueryExpression exp)
        {
            var multi = new MultiQuery();
            _console.WriteLine("(");
            var query1 = ProcessQueryExpression(exp.FirstQueryExpression) as Query;
            _console.WriteLine(")");
            _console.WriteLine(exp.BinaryQueryExpressionType.ToString());
            SetOperator op = 
                exp.BinaryQueryExpressionType == BinaryQueryExpressionType.Except
                ? SetOperator.Except
                : exp.BinaryQueryExpressionType == BinaryQueryExpressionType.Intersect
                    ? SetOperator.Intersect
                    : exp.BinaryQueryExpressionType == BinaryQueryExpressionType.Union
                        ? SetOperator.Union
                        : SetOperator.CartesianProduct; // This last case should not happen.
            _console.WriteLine("(");
            var query2 = ProcessQueryExpression(exp.SecondQueryExpression) as Query;
            _console.WriteLine(")");
            var tuple = new Tuple<dynamic, dynamic>(query1, query2);
            multi.Operators.Add(tuple, op);
            multi.Queries.Add(query1);
            multi.Queries.Add(query2);
            return multi;
        }

        public dynamic ProcessColumnReferenceExpression(ColumnReferenceExpression exp)
        {
            if (exp.ColumnType == ColumnType.Regular)
            {
                var att = new QueryModel.Attribute();
                if(exp.MultiPartIdentifier.Count == 1)
                {
                    att.Name = exp.MultiPartIdentifier.Identifiers[0].Value;
                }
                else if(exp.MultiPartIdentifier.Count == 2)
                {
                    att.Alias = exp.MultiPartIdentifier.Identifiers[0].Value;
                    att.Name = exp.MultiPartIdentifier.Identifiers[1].Value;
                }
                foreach (var i in exp.MultiPartIdentifier.Identifiers)
                {
                    _console.Write(i.Value + " ");
                }
                return att;
            }
            else if (exp.ColumnType == ColumnType.Wildcard)
            {
                _console.Write("*");
                return "*";
            }
            else
            {
                throw new Exception("ColumnReferenceExpression could not be identified.");
            }
        }

        public Tuple<List<Condition>, Dictionary<Tuple<Condition,Condition>,BooleanBinaryExpressionType>>
            ProcessBooleanBinaryExpression(BooleanBinaryExpression exp)
        {
            if (exp == null) return null;
            Condition left;
            Condition right;
            var conditions = new List<Condition>();
            var operators = new Dictionary<Tuple<Condition, Condition>, BooleanBinaryExpressionType>();
            
            if (exp.FirstExpression is BooleanComparisonExpression)
            {
                var leftExp = exp.FirstExpression as BooleanComparisonExpression;
                left = ProcessBooleanComparisonExpression(leftExp);
                conditions.Insert(0, left);
            }
            else if (exp.FirstExpression is BooleanBinaryExpression)
            {
                var leftExp = exp.FirstExpression as BooleanBinaryExpression;
                var tuple = ProcessBooleanBinaryExpression(leftExp);
                left = tuple.Item1.Last();
                foreach (var c in tuple.Item1)
                {
                    conditions.Add(c);
                }
                foreach (var t in tuple.Item2)
                {
                    operators.Add(t.Key, t.Value);
                }
            }
            else
            {
                throw new Exception();
            }

            _console.WriteLine(exp.BinaryExpressionType.ToString());
            var op = exp.BinaryExpressionType;
            if (exp.SecondExpression is BooleanComparisonExpression)
            {
                var rightExp = exp.SecondExpression as BooleanComparisonExpression;
                right = ProcessBooleanComparisonExpression(rightExp);
                var tuple = new Tuple<Condition, Condition>(left, right);
                //var kvp = new KeyValuePair<Tuple<Condition, Condition>, BooleanBinaryExpressionType>(tuple, op);
                operators.Add(tuple, op);
                conditions.Add(right);
            }
            //else if (exp.SecondExpression is BooleanBinaryExpression)
            //{
            //    var rightExp = exp.SecondExpression as BooleanBinaryExpression;
            //    var tuple = ProcessBooleanBinaryExpression(rightExp);
            //    right = tuple.Item1.First();
            //    foreach (var c in tuple.Item1)
            //    {
            //        conditions.Add(c);
            //    }
            //    foreach(var t in tuple.Item2)
            //    {
            //        operators.Add(t.Key, t.Value);
            //    }
            //}
            else
            {
                throw new Exception();
            }
            return new Tuple<List<Condition>, Dictionary<Tuple<Condition, Condition>, BooleanBinaryExpressionType>>(conditions, operators);
        }

        public Condition ProcessBooleanComparisonExpression(BooleanComparisonExpression exp)
        {
            var condition = new Condition();
            condition.LeftSide = ProcessScalarExpression(exp.FirstExpression);
            _console.Write(exp.ComparisonType + " ");
            condition.Operator = exp.ComparisonType;
            condition.RightSide = ProcessScalarExpression(exp.SecondExpression);
            _console.WriteLine(string.Empty);
            return condition;
        }

        public dynamic ProcessSelectScalarExpression(SelectScalarExpression exp)
        {
            var att = ProcessScalarExpression(exp.Expression);
            if (exp.ColumnName != null)
            {
                _console.Write(exp.ColumnName.Value);
            }
            _console.WriteLine(string.Empty);

            return att;
        }

        public dynamic ProcessScalarExpression(ScalarExpression exp)
        {
            if (exp is ColumnReferenceExpression)
            {
                return ProcessColumnReferenceExpression(exp as ColumnReferenceExpression);
            }
            else if (exp is FunctionCall)
            {
                var funcExp = exp as FunctionCall;
                var func = new Function();
                func.Type = funcExp.FunctionName.Value;

                _console.Write(funcExp.FunctionName.Value + "(");
                foreach (ScalarExpression p in funcExp.Parameters)
                {                    
                    var col = ProcessScalarExpression(p);
                    if(col is QueryModel.Attribute)
                    {
                        var att = col as QueryModel.Attribute;
                        func.Attributes.Add(att);
                    }
                    else if(col is string)
                    {
                        var att = col as string;
                        if(att == "*")
                        {
                            func.IsWildCard = true;
                            break;
                        }
                    }
                }
                _console.Write(") ");
                return func;
            }
            else if (exp is StringLiteral)
            {
                var text = exp as StringLiteral;
                _console.Write("\"" + text.Value + "\" ");
                return text.Value;
            }
            else if (exp is ScalarSubquery)
            {
                var subquery = exp as ScalarSubquery;
                _console.WriteLine(string.Empty);
                _console.WriteLine("(");
                var query = ProcessQuerySpecification(subquery.QueryExpression as QuerySpecification);
                _console.WriteLine(")");
                return query;
            }
            else if (exp is IntegerLiteral)
            {
                var integer = exp as IntegerLiteral;
                _console.Write(integer.Value);
                return integer.Value;
            }
            else
            {
                _console.WriteLine("Scalar expression not identified.");
                return 0;
            }
        }

        #endregion

        private void AssembleMultiQuery(MultiQuery result)
        {
            var queries = _lastQueryString.Split(_setOperators.ToArray(), StringSplitOptions.None).Select(q => q.Trim()).ToList();
            var tokens = _lastQueryString.Split(' ').ToList();
            var querySetOperators = tokens.Where(t => _setOperators.Contains(t)).ToList();

            foreach (var q in queries)
            {
                result.Queries.Add(new Query
                {
                    OriginalString = q
                });
            }

            if (queries.Count > 1)
            {
                for (var i = 1; i < result.Queries.Count; i++)
                {
                    var query1 = result.Queries[i - 1];
                    var query2 = result.Queries[i];
                    var normalizedOperator = querySetOperators[i - 1] + " ";
                    var op = MultiQuery.OperatorMap.Single(x => x.Value == normalizedOperator).Key;
                    result.Operators.Add(new Tuple<dynamic, dynamic>(query1, query2), op);
                }
            }
        }
        private void AssembleQuery(Query query)
        {
            var tokens = query.OriginalString.Split(' ').ToList();
            var fromIndex = 0;
            var whereIndex = 0;
            foreach (var t in tokens)
            {
                if(t == "from")
                {
                    fromIndex = tokens.IndexOf(t);
                }
                if (t == "where")
                {
                    whereIndex = tokens.IndexOf(t);
                    break;
                }
            }

            if(whereIndex > fromIndex)
            {
                query.From = GetFromStatement(tokens.GetRange(fromIndex, whereIndex - fromIndex));
                query.Select = GetSelectStatement(tokens.GetRange(0, fromIndex));
                query.Where = GetWhereStatement(tokens.GetRange(whereIndex, tokens.Count - whereIndex));
            }
            else
            {
                query.From = GetFromStatement(tokens.GetRange(fromIndex, tokens.Count - fromIndex));
                query.Select = GetSelectStatement(tokens.GetRange(0, fromIndex));
            }
        }
        private FromStatement GetFromStatement(ICollection<string> tokens)
        {
            tokens.Remove("from");
            var statement = new FromStatement();
            var statementString = new StringBuilder();
            foreach(var t in tokens)
            {
                statementString.Append(t + " ");
            }
            var relations = statementString.ToString().Contains(',') 
                ? statementString.ToString().Split(',').Select(t => t.Trim()).ToList() 
                : tokens.ToList();

            foreach (var r in relations)
            {
                Relation newRelation;
                var relationTokens = r.Split(' ').ToList();
                if(relationTokens.Contains("as"))
                {
                    newRelation = _schema.Relations.Single(rs =>
                        rs.Name == relationTokens[0]);
                    newRelation.Aliases.Add(relationTokens[2]);
                }
                else if (relationTokens.Count == 2)
                {
                    newRelation = _schema.Relations.Single(rs =>
                        rs.Name == relationTokens[0]);
                    newRelation.Aliases.Add(relationTokens[1]);
                }
                else
                {
                    newRelation = _schema.Relations.Single(rs =>
                        rs.Name == relationTokens[0]);
                    newRelation.Aliases.Add(relationTokens[0].Substring(0, 1));
                }

                statement.Relations.Add(newRelation);
            }
            statement.Relations = statement.Relations.OrderBy(r => r.Priority).ToList();
            return statement;
        }
        private QueryModel.SelectStatement GetSelectStatement(ICollection<string> tokens)
        {
            tokens.Remove("select");
            var statement = new QueryModel.SelectStatement();
            var statementString = String.Concat(tokens);
            var attributes = statementString.Contains(',') 
                ? statementString.Split(',').Select(t => t.Trim()).ToList() 
                : tokens.ToList();

            foreach(var a in attributes)
            {
                statement.Attributes.Add(GetAttribute(a));
            }

            return statement;
        }
        private WhereStatement GetWhereStatement(ICollection<string> tokens)
        {
            tokens.Remove("where");
            var statement = new WhereStatement();
            var statementString = new StringBuilder();
            foreach (var t in tokens)
            {
                statementString.Append(t + " ");
            }
            var conditions = tokens.Contains("and") || tokens.Contains("or")
                ? statementString.ToString()
                    .Split(WhereStatement.OperatorMap.Values.Select(v => " "+v.Trim()+" ").ToArray(), StringSplitOptions.None)
                    .Select(t => t.Trim()).ToList()
                : tokens.ToList();
            if(conditions.Count > 1)
            {
                var operators = tokens.Where(t => WhereStatement.OperatorMap.Values.Contains(t+" ")).ToList();
                for (var i = 1; i < conditions.Count; i++)
                {
                    if (i == 1)
                    {
                        List<string> leftSplit;
                        BooleanComparisonType leftConditionalOperator;
                        dynamic condition1LeftSide;
                        dynamic condition1RightSide;
                        if (conditions[i - 1].Contains(" "))
                        {
                            leftSplit = conditions[i - 1].Split(' ').Select(t => t.Trim()).ToList();
                            condition1LeftSide = leftSplit[0].Contains(".") ? GetAttribute(leftSplit[0]) : GetConditionValue(leftSplit[0]);
                            leftConditionalOperator = Condition.OperatorMap.Single(x => x.Value == leftSplit[1] + " ").Key;
                            condition1RightSide = leftSplit[2].Contains(".") ? GetAttribute(leftSplit[2]) : GetConditionValue(leftSplit[2]);
                        }
                        else
                        {
                            leftSplit = conditions[i - 1]
                                .Split(Condition.OperatorMap.Values.Select(v => v.Trim()).ToArray(), StringSplitOptions.None)
                                .Select(t => t.Trim()).ToList();
                            var leftExtraction = conditions[i - 1].Replace(leftSplit[0], "").Replace(leftSplit[1], "").Trim();
                            condition1LeftSide = leftSplit[0].Contains(".") ? GetAttribute(leftSplit[0]) : GetConditionValue(leftSplit[0]);
                            leftConditionalOperator = Condition.OperatorMap.Single(x => x.Value == leftExtraction + " ").Key;
                            condition1RightSide = leftSplit[1].Contains(".") ? GetAttribute(leftSplit[1]) : GetConditionValue(leftSplit[1]);
                        }
                        statement.Conditions.Add(new Condition
                        {
                            LeftSide = condition1LeftSide,
                            Operator = leftConditionalOperator,
                            RightSide = condition1RightSide,
                            QueryNumber = _queryCounter
                        });
                    }

                    List<string> rightSplit;
                    BooleanComparisonType rightConditionalOperator;
                    dynamic condition2LeftSide;
                    dynamic condition2RightSide;
                    if (conditions[i].Contains(" "))
                    {
                        rightSplit = conditions[i].Split(' ').Select(t => t.Trim()).ToList();
                        condition2LeftSide = rightSplit[0].Contains(".") ? GetAttribute(rightSplit[0]) : GetConditionValue(rightSplit[0]);
                        rightConditionalOperator = Condition.OperatorMap.Single(x => x.Value == rightSplit[1] + " ").Key;
                        condition2RightSide = rightSplit[1].Contains(".") ? GetAttribute(rightSplit[2]) : GetConditionValue(rightSplit[2]);
                    }
                    else
                    {
                        rightSplit = conditions[i]
                            .Split(Condition.OperatorMap.Values.Select(v => v.Trim()).ToArray(), StringSplitOptions.None)
                            .Select(t => t.Trim()).ToList();
                        condition2LeftSide = rightSplit[0].Contains(".") ? GetAttribute(rightSplit[0]) : GetConditionValue(rightSplit[0]);
                        var rightExtraction = conditions[i].Replace(rightSplit[0], "").Replace(rightSplit[1], "").Trim();
                        rightConditionalOperator = Condition.OperatorMap.Single(x => x.Value == rightExtraction + " ").Key;
                        condition2RightSide = rightSplit[1].Contains(".") ? GetAttribute(rightSplit[1]) : GetConditionValue(rightSplit[1]);
                    }
                    statement.Conditions.Add(new Condition
                    {
                        LeftSide = condition2LeftSide,
                        Operator = rightConditionalOperator,
                        RightSide = condition2RightSide,
                        QueryNumber = _queryCounter
                    });

                    var normalizedOperator = operators[i - 1] + " ";
                    var conditionsOperator = WhereStatement.OperatorMap.Single(x => x.Value == normalizedOperator).Key;
                    var leftCondition = statement.Conditions[i - 1];
                    var rightCondition = statement.Conditions[i];
                    statement.Operators.Add(new Tuple<Condition, Condition>(leftCondition, rightCondition), conditionsOperator);
                }
                return statement;
            }

            var split = conditions[0].Replace(" ", string.Empty)
                            .Split(Condition.OperatorMap.Values.Select(v => v.Trim()).ToArray(), StringSplitOptions.None)
                            .Select(t => t.Trim()).ToList();
            var op = Condition.OperatorMap.Single(x => x.Value == split[1] + " ").Key;
            statement.Conditions.Add(new Condition
            {
                LeftSide = split[0],
                Operator = op,
                RightSide = split[2]
            });

            return statement;
        }

        private dynamic GetConditionValue(string p)
        {
            double value;
            var isNumber = double.TryParse(p, out value);
            if (isNumber) return value;
            return p.Replace("'", "").Replace("’", "").Replace("`", "").Replace("‘", "");
        }

        private QueryModel.Attribute GetAttribute(string a)
        {
            var newAttribute = new QueryModel.Attribute
            {
                QueryNumber = _queryCounter
            };

            if (a.Contains("."))
            {
                var attributeTokens = a.Split('.');
                Relation relation;
                string alias;
                // Alias is relation name
                if (_schema.Relations.Any(t => t.Name == attributeTokens[0]))
                {
                    relation = _schema.Relations.Single(t => t.Name == attributeTokens[0]);
                    alias = relation.Aliases[_queryCounter];
                    newAttribute.Name = attributeTokens[1];
                }
                else
                {
                    relation = _schema.Relations.SingleOrDefault(r => 
                        r.Attributes.Any(t => 
                            t.Name == attributeTokens[1]) &&
                            r.Aliases.Contains(attributeTokens[0]));
                    if (relation == null)
                    {
                        throw new Exception("There is a conflict between your attribute aliases and the internal schema.");
                    }
                    alias = relation.Aliases[_queryCounter];
                }
                newAttribute.Alias = alias;
                newAttribute.Name = attributeTokens[1];
            }
            else if (a.Contains("as"))
            {
                var attributeTokens = a.Split(' ');
                var relation = _schema.Relations.SingleOrDefault(r => 
                    r.Attributes.Any(t => t.Name == attributeTokens[0]) &&
                    r.Aliases.Contains(attributeTokens[2]));
                if (relation == null)
                {
                    throw new Exception("There is a conflict between your attribute aliases and the internal schema.");
                }
                relation.Aliases.Add(attributeTokens[2]);
                newAttribute.Alias = attributeTokens[2];
                newAttribute.Name = attributeTokens[0];
            }
            else
            {
                var relation = _schema.Relations.SingleOrDefault(r => r.Attributes.Any(t => t.Name == a));
                if (relation == null)
                {
                    throw new Exception("There is a conflict between your attribute aliases and the internal schema.");
                }
                newAttribute.Alias = relation.Aliases[_queryCounter];
                newAttribute.Name = a;
            }

            return newAttribute;
        }
        
        private void PerformSimpleValidation(IEnumerable<Query> queries)
        {
            foreach (var q in queries)
            {
                var tokens = q.OriginalString.Split(' ');
                if (!tokens.Contains("select") || !tokens.Contains("from"))
                {
                    throw new Exception("Query is invalid.");
                }
            }
        }
    }
}
