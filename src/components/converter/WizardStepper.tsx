type StepState = 'done' | 'current' | 'pending';

type WizardStep = {
  id: string;
  label: string;
  state: StepState;
};

export function WizardStepper({ steps }: { steps: WizardStep[] }) {
  return (
    <section className="wizard-stepper" aria-label="Conversion steps">
      {steps.map((step) => (
        <div key={step.id} className={`wizard-step wizard-step-${step.state}`}>
          <span className="step-dot" />
          <span>{step.label}</span>
        </div>
      ))}
    </section>
  );
}
