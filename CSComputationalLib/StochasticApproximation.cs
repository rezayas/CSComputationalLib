﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.Data.Text;

namespace ComputationLib
{

    public abstract class SimModel
    {
        public abstract double GetAReplication(Vector<double> x, bool ifResampleSeeds = true);
        public virtual Vector<double> GetDerivativeEstimate(Vector<double> x, double derivative_step) { return null; }
        public virtual void ResetSeedAtItr0() { }
    }

    public class TestBedX2Y2XY : SimModel
    {
        RandomVariateLib.Normal _err; // a normally distributed error term
        RandomVariateLib.RNG _rnd;
        int _currentSeed = 0;

        public TestBedX2Y2XY(double errorVar)
        {
            _err = new RandomVariateLib.Normal("Error term", 0, errorVar);
            _rnd = new RandomVariateLib.RNG(_currentSeed);
        }

        public override double GetAReplication(Vector<double> x, bool ifResampleSeeds)
        {
            if (ifResampleSeeds)
            {
                ++_currentSeed;
                _rnd = new RandomVariateLib.RNG(_currentSeed);
            }

            return Math.Pow(x[0], 2) + Math.Pow(x[1], 2) + x[0]*x[1] + _err.SampleContinuous(_rnd);
        }
        public override void ResetSeedAtItr0()
        {
            _currentSeed = 0;
        }
    }

    public class StepSize_a
    {
        // step_n = a * b / (b + n) for n >= 0, a > 0, and b >= 1

        public double a0 { get; }
        public double b { get; }

        public StepSize_a(double a0, double b=0)
        {
            this.a0 = a0;
        }
        public double GetValue(int itr)
        {
            return a0 / (itr+1);
        }
    }

    public class StepSize_Df
    {
        public double c0 { get; }

        public StepSize_Df(double c0)
        {
            this.c0 = c0;
        }
        public double GetValue(int itr)
        {
            return c0 * Math.Pow(itr+1, -0.25);
        }
    }

    public class StochasticApproximation
    {
        private StepSize_a _stepSize_a = null;
        private StepSize_Df _stepSize_Df = null;
        private SimModel _simModel = null;

        public List<int> Itr_i { get; private set; } = new List<int>();                
        public List<Vector<double>> Itr_x { get; private set; } = new List<Vector<double>>();
        public List<double> Itr_f { get; private set; } = new List<double>();
        public List<Vector<double>> Itr_Df { get; private set; } = new List<Vector<double>>();
        public List<double> Itr_step_Df { get; private set; } = new List<double>();
        public List<double> Itr_step_a { get; private set; } = new List<double>();

        public Vector<double> xStar { get; private set; }
        public double fStar { get; private set; }

        public StochasticApproximation(SimModel simModel, StepSize_a stepSize_a, StepSize_Df stepSize_Df)
        {
            _simModel = simModel;
            _stepSize_a = stepSize_a;
            _stepSize_Df = stepSize_Df;
        }

        public void Minimize(int maxItrs, int nLastItrsToAve, Vector<double> x0, 
            bool ifTwoSidedDerivative = true, bool modelProvidesDerivatives = false)
        {
            // reset seed of the simulation model at iteration 0
            // note that this method could be empty if there is no need to reset the seed 
            _simModel.ResetSeedAtItr0();

            // iteration 0
            Vector<double> x = x0;
            double f;         

            // iterations of the algorithm
            for (int itr = 0; itr < maxItrs; itr++)
            {
                // get f(x)
                f = _simModel.GetAReplication(x, ifResampleSeeds: true);

                // current derivative step size
                double step_Df = _stepSize_Df.GetValue(itr);                

                // estimate the derivative of f at x
                Vector<double> Df = Vector<double>.Build.Dense(x0.Count());

                // calcualte derivative 
                if (modelProvidesDerivatives)
                {
                    // get the derivative from the model
                    Df = _simModel.GetDerivativeEstimate(x, step_Df);
                    if (Df.Norm(2) <= 0 && Itr_x.Count > 1)
                    {
                        //x = Itr_x[Itr_x.Count - 1];
                        //Df = _simModel.GetDerivativeEstimate(x, step_Df);
                    }
                }
                else
                {
                    // build epsilon matrix
                    Matrix<double> epsilonMatrix = Matrix<double>.Build.DenseDiagonal(x0.Count(), step_Df);

                    for (int i = 0; i < x0.Count(); i++)
                    {
                        if (ifTwoSidedDerivative)
                        {                         
                            // estimate the derivative here
                            Df[i] =
                                (
                                _simModel.GetAReplication(x + epsilonMatrix.Row(i), ifResampleSeeds: false) -
                                _simModel.GetAReplication(x - epsilonMatrix.Row(i), ifResampleSeeds: false)
                                ) / (2 * step_Df);
                        }
                        else
                        {
                            Df[i] =
                                (_simModel.GetAReplication(x + epsilonMatrix.Row(i), ifResampleSeeds: false) - f)
                                / step_Df;
                        }
                    }
                }

                // normalize derivative
                Vector<double> nDf = Df.Normalize(p: 2);
                
                // find step size
                double step_a = _stepSize_a.GetValue(itr);

                // store information of this iteration 
                Itr_i.Add(itr);
                Itr_x.Add(x);
                Itr_f.Add(f);
                Itr_Df.Add(nDf);
                Itr_step_Df.Add(step_Df);
                Itr_step_a.Add(step_a);

                // find a new x: x_new = x - stepSize*f'(x)
                x = x - step_a * nDf;              
            }

            // store the optimal x and optimal objective value 
            double fSum = 0;
            Vector<double> xSum = Vector<double>.Build.Dense(x0.Count);   
            for (int itr = maxItrs; itr > maxItrs - nLastItrsToAve; itr--)
            {
                fSum += Itr_f[itr - 1];
                xSum += Itr_x[itr - 1];
            }
            xStar = xSum / nLastItrsToAve;
            fStar = fSum / nLastItrsToAve;
        }

        public double[,] GetResultsInAMatrix()
        {
            double[,] result = new double[Itr_i.Count, 2 + 2*xStar.Count + 2];

            for (int itr = 0; itr<Itr_i.Count; itr++)
            {
                int j = 0;

                // iteration
                result[itr, j++] = Itr_i[itr];
                // f
                result[itr, j++] = Itr_f[itr];

                // x
                for (int i = 0; i < xStar.Count; i++)
                    result[itr, j++] = Itr_x[itr][i];
                // Df(x)
                for (int i = 0; i < xStar.Count; i++)
                    result[itr, j++] = Itr_Df[itr][i];
                
                // steps
                result[itr, j++] = Itr_step_Df[itr];
                result[itr, j++] = Itr_step_a[itr];
            }
            return result;
        }

        public void ExportResultsToCSV(string filename)
        {
            Matrix<double> matrix = Matrix<double>.Build.DenseOfArray(GetResultsInAMatrix());

            List<string> colHeader = new List<string>();
            colHeader.Add("Iteration");
            colHeader.Add("f");
            for (int i = 0; i < xStar.Count; i++)
                colHeader.Add("x"+i);
            for (int i = 0; i < xStar.Count; i++)
                colHeader.Add("Df" + i);
            colHeader.Add("Step_Df");
            colHeader.Add("Step_a");

            DelimitedWriter.Write(filename, matrix, ",", columnHeaders: colHeader);
        }

        public string Get_a_c()
        {
            return "a" + _stepSize_a.a0 + "-c" + _stepSize_Df.c0; //.ToString("F2")
        }
        public double Get_a0()
        {
            return _stepSize_a.a0;
        }
        public double Get_c0()
        {
            return _stepSize_Df.c0;
        }
    }

    public class ParallelStochasticApproximation
    {
        List<StochasticApproximation> stochasticApproximations = new List<StochasticApproximation>();
        public double fStar { get; private set; } = double.MaxValue;
        public Vector<double> xStar { get; private set; }
        public double aStar { get; private set; } = double.NaN;
        public double cStar { get; private set; } = double.NaN;

        public ParallelStochasticApproximation(List<SimModel> simModels, double[] stepSize_as, double[] stepSize_cs)
        {

            // build the stochastic approximations
            int i = 0;
            foreach (double a in stepSize_as)
            {
                foreach (double c in stepSize_cs)
                {
                    stochasticApproximations.Add(
                        new StochasticApproximation(
                            simModel: simModels[i++],
                            stepSize_a: new StepSize_a(a),
                            stepSize_Df: new StepSize_Df(c))
                            );                    
                }
            }
        }

        public void Minimize(int maxItrs, int nLastItrsToAve, Vector<double> x0, 
            bool ifTwoSidedDerivative = true, bool ifParallel = true, bool modelProvidesDerivatives = false)
        {
            if (ifParallel && stochasticApproximations.Count > 1)
            {
                Parallel.ForEach(stochasticApproximations, stocApprx =>
                {
                    stocApprx.Minimize(maxItrs, nLastItrsToAve, x0, ifTwoSidedDerivative, modelProvidesDerivatives);
                });
            }
            else
            {
                foreach (StochasticApproximation stocApprx in stochasticApproximations)
                {
                    stocApprx.Minimize(maxItrs, nLastItrsToAve, x0, ifTwoSidedDerivative, modelProvidesDerivatives);
                }
            }

            // find the optimizer
            // find the a value that minimizes f
            xStar = Vector<double>.Build.Dense(x0.Count);
            fStar = double.MaxValue;
            foreach (StochasticApproximation stocApprx in stochasticApproximations)
            {
                // if this a led to the minimum f
                if (stocApprx.fStar < fStar)
                {
                    fStar = stocApprx.fStar;
                    xStar = stocApprx.xStar;
                    aStar = stocApprx.Get_a0();
                    cStar = stocApprx.Get_c0();
                }
            }
        }

        public void ExportResultsToCSV(string filename)
        {
            foreach (StochasticApproximation stocApprx in stochasticApproximations)
            {
                stocApprx.ExportResultsToCSV(filename + stocApprx.Get_a_c() + ".csv");
            }
        }
    }
}
