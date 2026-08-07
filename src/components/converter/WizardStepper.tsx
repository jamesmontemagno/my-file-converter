type StepState = 'done' | 'current' | 'pending';

type WizardStep = {
  id: string;
  label: string;
  state: StepState;
};

export function WizardStepper({ steps }: { steps: WizardStep[] }) {
  return (
    <ol className="wizard-stepper" aria-label="Conversion steps">
      {steps.map((step) => (
        <li
          key={step.id}
          className={`wizard-step wizard-step-${step.state}`}
          aria-current={step.state === 'current' ? 'step' : undefined}
        >
          <span className="step-dot" />
          <span>{step.label}</span>
        </li>
      ))}
    </ol>
  );
}
