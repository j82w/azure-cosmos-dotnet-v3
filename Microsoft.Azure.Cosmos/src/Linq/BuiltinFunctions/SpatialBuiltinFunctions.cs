﻿//-----------------------------------------------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//-----------------------------------------------------------------------------------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Linq
{
    using Microsoft.Azure.Cosmos.Spatial;
    using Microsoft.Azure.Cosmos.Sql;
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using static Microsoft.Azure.Cosmos.Linq.FromParameterBindings;

    internal static class SpatialBuiltinFunctions
    {
        private static Dictionary<string, BuiltinFunctionVisitor> SpatialBuiltinFunctionDefinitions { get; set; }

        static SpatialBuiltinFunctions()
        {
            SpatialBuiltinFunctionDefinitions = new Dictionary<string, BuiltinFunctionVisitor>();

            SpatialBuiltinFunctionDefinitions.Add("Distance",
                new SqlBuiltinFunctionVisitor("ST_Distance",
                    true,
                    new List<Type[]>()
                    {
                        new Type[]{typeof(Geometry), typeof(Geometry)},
                    }));

            SpatialBuiltinFunctionDefinitions.Add("Within",
                new SqlBuiltinFunctionVisitor("ST_Within",
                    true,
                    new List<Type[]>()
                    {
                        new Type[]{typeof(Geometry), typeof(Geometry)},
                    }));

            SpatialBuiltinFunctionDefinitions.Add("IsValidDetailed",
                new SqlBuiltinFunctionVisitor("ST_IsValidDetailed",
                    true,
                    new List<Type[]>()
                    {
                        new Type[]{typeof(Geometry)},
                    }));

            SpatialBuiltinFunctionDefinitions.Add("IsValid",
                new SqlBuiltinFunctionVisitor("ST_IsValid",
                    true,
                    new List<Type[]>()
                    {
                        new Type[]{typeof(Geometry)},
                    }));

            SpatialBuiltinFunctionDefinitions.Add("Intersects",
                new SqlBuiltinFunctionVisitor("ST_Intersects",
                    true,
                    new List<Type[]>()
                    {
                        new Type[]{typeof(Geometry), typeof(Geometry)},
                    }));
        }

        public static SqlScalarExpression Visit(MethodCallExpression methodCallExpression, TranslationContext context)
        {
            BuiltinFunctionVisitor visitor = null;
            if (SpatialBuiltinFunctionDefinitions.TryGetValue(methodCallExpression.Method.Name, out visitor))
            {
                return visitor.Visit(methodCallExpression, context);
            }

            throw new DocumentQueryException(string.Format(CultureInfo.CurrentCulture, ClientResources.MethodNotSupported, methodCallExpression.Method.Name));
        }
    }
}
