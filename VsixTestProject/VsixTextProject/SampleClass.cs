using System;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using Npgsql;
using NpgsqlTypes;
namespace VsixTextProject
{
    public class SampleClass
    {​​​​​​​
        public SampleClass(SqlConnection connection, SqlCommand c,
                SqlDataAdapter da, SqlDataReader dr, SqlParameter p, SqlException e)
        {

        }

        public SqlDataAdapter DA { get; set; }
        public SqlDataReader DR { get; set; }
        public SqlParameter P { get; set; }
        public SqlException Ex { get; set; }
        public SqlConnection MyProperty { get; set; }
        public SqlCommand MyProperty2 { get; set; }

        SqlConnection conn = null;
        String connstring = "";
        public void Test(string firstName, string lastName, int age)
        {
            conn = new SqlConnection(connstring);
            conn.Open();

            SqlCommand comm = new SqlCommand();
            comm.Connection = conn;

            //Creating instance of SqlParameter  
            SqlParameter PmtrRollNo = new SqlParameter();
            PmtrRollNo.ParameterName = "@rn"; // Defining Name  
            PmtrRollNo.SqlDbType = SqlDbType.Int; // Defining DataType  
            PmtrRollNo.Direction = ParameterDirection.Input; // Setting the direction  

            //Creating instance of SqlParameter  
            SqlParameter PmtrName = new SqlParameter();
            PmtrName.ParameterName = "@nm"; // Defining Name  
            PmtrName.SqlDbType = SqlDbType.VarChar; // Defining DataType  
            PmtrName.Size = 30;
            PmtrName.Direction = ParameterDirection.Output; // Setting the direction  

            //Creating instance of SqlParameter  
            SqlParameter PmtrCity = new SqlParameter("@ct", SqlDbType.VarChar, 20);
            PmtrCity.Direction = ParameterDirection.Output; // Setting the direction  

            // Adding Parameter instances to sqlcommand  

            comm.Parameters.Add(PmtrRollNo);
            comm.Parameters.Add(PmtrName);
            comm.Parameters.Add(PmtrCity);

            // Setting values of Parameter  


            comm.CommandText = "select @nm=name,@ct=city from student_detail where rollno=@rn";

            try
            {
                comm.ExecuteNonQuery();
            }
            catch (Exception)
            {
            }
            finally
            {
                conn.Close();
            }
        }

        public void PGSample()
        {​​​​​​​
			using (var con = new SqlConnection())
            {​​​​​​​
				using (var command = new SqlCommand("selet * from employees", con))
                {​​​​​​​
					int i = 0;
                }​​​​​​​


				string strQuery = "selet * from employees2";
                using (var command2 = new SqlCommand(strQuery, con))
                {​​​​​​​
					int i = 0;
                }​​​​​​​
			}​​​​​​​

			var abc = "Sandeep";
        }​​​​​​​


		public void PGSample2()
        {​​​​​​​
			StringBuilder sb = new StringBuilder();

            sb.Append("select * ");
            sb.Append("from ");
            sb.Append("Departments ");


            using (var con = new SqlConnection())
            {​​​​​​​
				using (var command = new SqlCommand(sb.ToString(), con))
                {​​​​​​​
					int i = 0;
                }​​​​​​​

				string strQuery = "selet * from employees2";
                using (var command2 = new SqlCommand(strQuery, con))
                {​​​​​​​
					int i = 0;
                }​​​​​​​
			}​​​​​​​

			var abc = "Sandeep";
        }​​​​​​​


        private string GetUserNumber()
        {​​​​​​​
            string userNo = string.Empty;
            string RRPTConnString = "";
            using (var conn = new SqlConnection(RRPTConnString))
            {​​​​​​​
                string cmdText = string.Format("SELECT userno from users where username = '{​​​​​​​0}​​​​​​​'", "CurrentUserName");
                using (var cmd = new SqlCommand(cmdText, conn))
                {​​​​​​​
                    cmd.CommandTimeout = 0;
                    cmd.CommandType = CommandType.Text;
                    conn.Open();

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {​​​​​​​
                        if (reader.Read())
                        {​​​​​​​
                            userNo = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader[0]);
                        }​​​​​​​
                    }​​​​​​​

                    conn.Close();
                }​​​​​​​
            }​​​​​​​

            return userNo.Trim();
        }​​​​​​​
    }
}
